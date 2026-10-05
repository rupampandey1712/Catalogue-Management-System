using Catalogue.Application;
using Catalogue.Domain;
using Catalogue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.UnitTests;

public class IdentityDomainTests
{
    [Theory]
    [InlineData("short1A", false)]
    [InlineData("alllowercase123", false)]
    [InlineData("ALLUPPERCASE123", false)]
    [InlineData("NoDigitsInHere", false)]
    [InlineData("Heat-Pump-2026", true)]
    public void Password_policy_requires_length_mixed_case_and_digit(string password, bool ok)
        => Assert.Equal(ok, PasswordPolicy.Check(password).Count == 0);

    [Fact]
    public void Password_must_not_contain_the_email_name()
    {
        Assert.Contains(PasswordPolicy.Check("Jsmith2026xx", "jsmith@briteheat.co.uk"), p => p.Contains("email"));
        Assert.Empty(PasswordPolicy.Check("Boiler-Room-77", "jsmith@briteheat.co.uk"));
    }

    [Fact]
    public void Account_locks_after_five_failures_and_unlocks_after_the_window()
    {
        var now = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        var user = new AppUser("Buyer@Example.com", "Buyer", ["Buyer"], 1);
        Assert.Equal("buyer@example.com", user.Email);
        for (var i = 0; i < AppUser.MaxFailedLogins - 1; i++) user.LoginFailed(now);
        Assert.False(user.IsLockedOut(now));
        user.LoginFailed(now);
        Assert.True(user.IsLockedOut(now.AddMinutes(14)));
        Assert.False(user.IsLockedOut(now.Add(AppUser.LockoutDuration).AddSeconds(1)));
    }

    [Fact]
    public void Password_change_and_disable_rotate_the_security_stamp()
    {
        var user = new AppUser("a@b.com", "A", ["Buyer"], 1);
        var stamp = user.SecurityStamp;
        user.SetPasswordHash("hash");
        Assert.NotEqual(stamp, user.SecurityStamp);
        stamp = user.SecurityStamp;
        user.Disable();
        Assert.NotEqual(stamp, user.SecurityStamp);
    }

    [Fact]
    public void Company_must_buy_or_sell_and_only_suspended_companies_reactivate()
    {
        Assert.Throws<ArgumentException>(() => new Organization("X Ltd", OrganizationCapability.None, "GB", null));
        var org = new Organization("BriteHeat Installers Ltd", OrganizationCapability.Buyer | OrganizationCapability.Seller, "gb", " gb123 ");
        Assert.Equal(OrganizationStatus.PendingVerification, org.Status);
        Assert.True(org.CanBuy && org.CanSell);
        Assert.Equal("GB123", org.VatNumber);
        Assert.Throws<InvalidOperationException>(() => org.Reactivate("admin"));
        org.Approve("admin", 7, 6);
        Assert.Equal(OrganizationStatus.Active, org.Status);
        org.Suspend("admin", "unpaid");
        org.Reactivate("admin");
        Assert.Equal(OrganizationStatus.Active, org.Status);
    }

    [Fact]
    public void Supplier_discount_is_bounded_and_scoped_to_buyer_and_dates()
    {
        var now = DateTime.UtcNow;
        Assert.Throws<ArgumentOutOfRangeException>(() => new SupplierDiscount(1, null, 95, now, null, null));
        var forOne = new SupplierDiscount(1, 42, 6, now.AddDays(-1), now.AddDays(1), null);
        Assert.True(forOne.AppliesTo(42, now));
        Assert.False(forOne.AppliesTo(43, now));
        Assert.False(forOne.AppliesTo(42, now.AddDays(2)));
        Assert.True(new SupplierDiscount(1, null, 2.5m, now.AddDays(-1), null, null).AppliesTo(99, now));
    }

    [Fact]
    public void Purchase_order_follows_its_status_machine()
    {
        var po = new PurchaseOrder("PO-1", 1, 1, 2, "GBP", "buyer", null, null);
        po.AddLine(new PurchaseOrderLine(10, 100, "S-1", "Boiler", 2, 450m, 500m, "GBP", ""));
        Assert.Equal(900m, po.Subtotal);
        Assert.Equal(1000m, po.ListValue);
        Assert.Throws<InvalidOperationException>(() => po.AddLine(new PurchaseOrderLine(11, 101, "S-2", "Valve", 1, 5m, null, "EUR", "")));
        Assert.Throws<InvalidOperationException>(() => po.TransitionTo(PurchaseOrderStatus.Shipped, null));
        po.TransitionTo(PurchaseOrderStatus.Accepted, null);
        po.TransitionTo(PurchaseOrderStatus.Shipped, "DPD 123");
        Assert.Throws<InvalidOperationException>(() => po.TransitionTo(PurchaseOrderStatus.Cancelled, null));
    }
}

public class MarketplacePricingTests
{
    private sealed record World(CatalogueDbContext Db, long ProductId, long Customer, long Own, long Hvx, long Nwd, long Cheap);

    /// <summary>
    /// One boiler (list £100) offered by four sellers: the content owner (list price), HVX (£95, 10% off
    /// for this buyer), NWD (£90 but sold in packs of 5) and the buyer's own company (£50).
    /// </summary>
    private static async Task<World> SeedAsync(decimal? tierPercent = null)
    {
        var db = new CatalogueDbContext(new DbContextOptionsBuilder<CatalogueDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var own = new Supplier("OWN", "Catalogue Direct", IntegrationType.Csv);
        var hvx = new Supplier("HVX", "HVAC Express", IntegrationType.Edi);
        var nwd = new Supplier("NWD", "Northwest Distribution", IntegrationType.Edi);
        var self = new Supplier("SELF", "Buyer's own stock", IntegrationType.Csv);
        db.Suppliers.AddRange(own, hvx, nwd, self);
        PriceList? tier = tierPercent is { } t ? new PriceList("TIER", "Tier", PriceListKind.Tier, t) : null;
        if (tier is not null) db.PriceLists.Add(tier);
        await db.SaveChangesAsync();
        var customer = new CustomerAccount("ACC-1", "BriteHeat", tier?.Id);
        db.CustomerAccounts.Add(customer);

        var product = new Product("BLR-1", "Combi boiler 30kW", supplierId: own.Id);
        product.Activate();
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.ProductPrices.Add(ProductPrice.For(product, "GBP", 100m, DateTime.UtcNow.AddDays(-1)));

        SupplierOffer Offer(Supplier s, decimal? price, int pack = 1)
        {
            var o = new SupplierOffer(product, s.Id, $"{s.Code}-BLR-1");
            o.UpdateBySeller(price, 50, 2, 1, pack, false);
            db.SupplierOffers.Add(o);
            return o;
        }
        Offer(own, null).MarkPreferred(true);
        Offer(hvx, 95m);
        Offer(nwd, 90m, pack: 5);
        Offer(self, 50m);
        db.SupplierDiscounts.Add(new SupplierDiscount(hvx.Id, customer.Id, 10, DateTime.UtcNow.AddDays(-1), null, "loyalty"));
        await db.SaveChangesAsync();
        return new World(db, product.Id, customer.Id, own.Id, hvx.Id, nwd.Id, self.Id);
    }

    [Fact]
    public async Task Cheapest_offer_is_chosen_after_supplier_discount_pack_rounding_and_excluding_own_stock()
    {
        var w = await SeedAsync();
        var quotes = (await new MarketplacePricing(w.Db).CompareAsync(new Dictionary<long, int> { [w.ProductId] = 3 }, w.Customer, w.Cheap, default))[w.ProductId];

        var hvx = quotes.Single(q => q.SupplierId == w.Hvx);
        Assert.Equal(85.50m, hvx.NetUnitPrice);          // 95 − 10%
        Assert.Equal(256.50m, hvx.LineTotal);
        Assert.True(hvx.IsCheapest);

        var nwd = quotes.Single(q => q.SupplierId == w.Nwd);
        Assert.Equal(5, nwd.OrderQuantity);               // 3 rounded up to a pack of 5
        Assert.Equal(450m, nwd.LineTotal);                // lower unit price, higher line total

        var mine = quotes.Single(q => q.SupplierId == w.Cheap);
        Assert.False(mine.Purchasable);
        Assert.Equal("Your own offer", mine.NotPurchasableReason);

        Assert.Equal(100m, quotes.Single(q => q.SupplierId == w.Own).NetUnitPrice);
    }

    [Fact]
    public async Task Tier_discount_applies_to_every_seller_before_their_own_discount()
    {
        var w = await SeedAsync(tierPercent: 20);
        var quotes = (await new MarketplacePricing(w.Db).CompareAsync(new Dictionary<long, int> { [w.ProductId] = 1 }, w.Customer, null, default))[w.ProductId];
        Assert.Equal(68.40m, quotes.Single(q => q.SupplierId == w.Hvx).NetUnitPrice); // 95 −20% −10%
        Assert.Equal(72.00m, quotes.Single(q => q.SupplierId == w.Nwd).NetUnitPrice); // 90 −20%
        Assert.Equal(80.00m, quotes.Single(q => q.SupplierId == w.Own).NetUnitPrice); // list 100 −20%
    }

    [Fact]
    public async Task Discount_for_another_buyer_does_not_apply()
    {
        var w = await SeedAsync();
        var quotes = (await new MarketplacePricing(w.Db).CompareAsync(new Dictionary<long, int> { [w.ProductId] = 1 }, w.Customer + 100, null, default))[w.ProductId];
        Assert.Equal(95m, quotes.Single(q => q.SupplierId == w.Hvx).NetUnitPrice);
        Assert.True(quotes.Single(q => q.SupplierId == w.Cheap).IsCheapest); // not their own company here
    }

    [Fact]
    public async Task Basket_becomes_one_purchase_order_per_supplier_at_server_prices()
    {
        var w = await SeedAsync();
        var service = new SourcingService(w.Db, new MarketplacePricing(w.Db));
        var nwdOffer = await w.Db.SupplierOffers.Where(o => o.SupplierId == w.Nwd).Select(o => o.Id).SingleAsync();

        var quote = await service.QuoteAsync([new BasketLine(w.ProductId, 3, null)], w.Customer, w.Cheap, default);
        Assert.Equal(256.50m, quote.Total);
        Assert.Equal(300m - 256.50m, quote.Savings);      // versus buying from the content owner

        // Buyer explicitly picks NWD for a second identical line: lines merge, the last pick wins.
        var orders = await service.PlaceAsync([new BasketLine(w.ProductId, 3, null), new BasketLine(w.ProductId, 2, nwdOffer)],
            buyerOrganizationId: 1, w.Customer, w.Cheap, "buyer", "REF-1", null, default);
        var po = Assert.Single(orders);
        Assert.Equal(w.Nwd, po.SupplierId);
        Assert.Equal(5, po.Lines.Single().Quantity);
        Assert.Equal(450m, po.Subtotal);
        Assert.Equal(PurchaseOrderStatus.Submitted, po.Status);
        Assert.Single(w.Db.AuditEvents.Where(a => a.Action == "PurchaseOrdersPlaced"));
    }

    [Fact]
    public async Task Ordering_only_your_own_stock_is_refused()
    {
        var w = await SeedAsync();
        foreach (var o in w.Db.SupplierOffers.Where(o => o.SupplierId != w.Cheap)) w.Db.SupplierOffers.Remove(o);
        await w.Db.SaveChangesAsync();
        var service = new SourcingService(w.Db, new MarketplacePricing(w.Db));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PlaceAsync([new BasketLine(w.ProductId, 1, null)], 1, w.Customer, w.Cheap, "buyer", null, null, default));
    }
}
