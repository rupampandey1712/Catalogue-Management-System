namespace Catalogue.Domain;

/// <summary>
/// A discount a selling company grants on its own offers - to one buyer account, or to every buyer
/// when <see cref="CustomerAccountId"/> is null. Stacks on top of the buyer's platform price list.
/// </summary>
public sealed class SupplierDiscount
{
    public long Id { get; private set; }
    public long SupplierId { get; private set; }
    public Supplier? Supplier { get; private set; }
    public long? CustomerAccountId { get; private set; }
    public CustomerAccount? CustomerAccount { get; private set; }
    public decimal DiscountPercent { get; private set; }
    public string? Note { get; private set; }
    public DateTime ValidFromUtc { get; private set; }
    public DateTime? ValidToUtc { get; private set; }
    public bool IsActive { get; private set; } = true;

    private SupplierDiscount() { }

    public SupplierDiscount(long supplierId, long? customerAccountId, decimal discountPercent, DateTime validFromUtc, DateTime? validToUtc, string? note)
    {
        if (discountPercent is <= 0 or > 90) throw new ArgumentOutOfRangeException(nameof(discountPercent), "Discount must be between 0 and 90%.");
        if (validToUtc is not null && validToUtc <= validFromUtc) throw new ArgumentException("ValidTo must be after ValidFrom.");
        SupplierId = supplierId;
        CustomerAccountId = customerAccountId;
        DiscountPercent = discountPercent;
        ValidFromUtc = validFromUtc;
        ValidToUtc = validToUtc;
        Note = note;
    }

    public bool AppliesTo(long? customerAccountId, DateTime now) =>
        IsActive && ValidFromUtc <= now && (ValidToUtc is null || ValidToUtc > now)
        && (CustomerAccountId is null || CustomerAccountId == customerAccountId);

    public void Deactivate() => IsActive = false;
}

public enum PurchaseOrderStatus { Submitted, Accepted, Rejected, Shipped, Cancelled }

/// <summary>
/// A purchase order from a buying company to one selling company (a basket spanning several suppliers
/// becomes one PO per supplier). Prices are frozen at submission. No payment is taken - settlement
/// happens on the companies' trade accounts, as is normal in B2B distribution.
/// </summary>
public sealed class PurchaseOrder
{
    public long Id { get; private set; }
    public string Number { get; private set; } = null!;
    public long BuyerOrganizationId { get; private set; }
    public Organization? BuyerOrganization { get; private set; }
    public long? CustomerAccountId { get; private set; }
    public long SupplierId { get; private set; }
    public Supplier? Supplier { get; private set; }
    public PurchaseOrderStatus Status { get; private set; } = PurchaseOrderStatus.Submitted;
    public string Currency { get; private set; } = "GBP";
    public decimal Subtotal { get; private set; }
    public decimal ListValue { get; private set; }
    public string? BuyerReference { get; private set; }
    public string? Note { get; private set; }
    public string CreatedBy { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; private set; } = DateTime.UtcNow;
    public string? StatusNote { get; private set; }
    /// <summary>Unpaid = settle on trade account (the default in B2B) unless the buyer pays online.</summary>
    public OrderPaymentStatus PaymentStatus { get; private set; } = OrderPaymentStatus.Unpaid;
    public DateTime? PaidAtUtc { get; private set; }
    public List<PurchaseOrderLine> Lines { get; } = [];

    private PurchaseOrder() { }

    public PurchaseOrder(string number, long buyerOrganizationId, long? customerAccountId, long supplierId, string currency,
        string createdBy, string? buyerReference, string? note)
    {
        Number = number;
        BuyerOrganizationId = buyerOrganizationId;
        CustomerAccountId = customerAccountId;
        SupplierId = supplierId;
        Currency = currency;
        CreatedBy = createdBy;
        BuyerReference = buyerReference;
        Note = note;
    }

    public void AddLine(PurchaseOrderLine line)
    {
        if (line.Currency != Currency) throw new InvalidOperationException("All lines of a purchase order must share its currency.");
        Lines.Add(line);
        Subtotal = Lines.Sum(l => l.LineTotal);
        ListValue = Lines.Sum(l => (l.ListUnitPrice ?? l.UnitPrice) * l.Quantity);
    }

    private static readonly Dictionary<PurchaseOrderStatus, PurchaseOrderStatus[]> Allowed = new()
    {
        [PurchaseOrderStatus.Submitted] = [PurchaseOrderStatus.Accepted, PurchaseOrderStatus.Rejected, PurchaseOrderStatus.Cancelled],
        [PurchaseOrderStatus.Accepted] = [PurchaseOrderStatus.Shipped, PurchaseOrderStatus.Cancelled],
        [PurchaseOrderStatus.Rejected] = [],
        [PurchaseOrderStatus.Shipped] = [],
        [PurchaseOrderStatus.Cancelled] = [],
    };

    /// <summary>Online payment is possible while the order is live and not already paid.</summary>
    public bool CanBePaid => Status is PurchaseOrderStatus.Submitted or PurchaseOrderStatus.Accepted
        && PaymentStatus is OrderPaymentStatus.Unpaid or OrderPaymentStatus.Pending && Subtotal > 0;

    public void MarkPaymentPending()
    {
        if (!CanBePaid) throw new InvalidOperationException($"Order {Number} cannot be paid ({Status}, {PaymentStatus}).");
        PaymentStatus = OrderPaymentStatus.Pending;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkPaid(DateTime now)
    {
        PaymentStatus = OrderPaymentStatus.Paid;
        PaidAtUtc ??= now;
        UpdatedAtUtc = now;
    }

    /// <summary>A failed or abandoned attempt leaves the order payable again.</summary>
    public void MarkPaymentAbandoned()
    {
        if (PaymentStatus == OrderPaymentStatus.Pending) PaymentStatus = OrderPaymentStatus.Unpaid;
    }

    public void MarkRefunded()
    {
        PaymentStatus = OrderPaymentStatus.Refunded;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Seller accepts/rejects/ships; buyer cancels. Anything else is an invalid transition.</summary>
    public void TransitionTo(PurchaseOrderStatus next, string? note)
    {
        if (!Allowed[Status].Contains(next))
            throw new InvalidOperationException($"A {Status.ToString().ToLowerInvariant()} order cannot be {next.ToString().ToLowerInvariant()}.");
        Status = next;
        StatusNote = note;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}

public sealed class PurchaseOrderLine
{
    public long Id { get; private set; }
    public long PurchaseOrderId { get; private set; }
    public long ProductId { get; private set; }
    public long OfferId { get; private set; }
    public string SupplierSku { get; private set; } = null!;
    public string Description { get; private set; } = null!;
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal? ListUnitPrice { get; private set; }
    public string Currency { get; private set; } = "GBP";
    public string PriceExplanation { get; private set; } = "";
    public decimal LineTotal { get; private set; }

    private PurchaseOrderLine() { }

    public PurchaseOrderLine(long productId, long offerId, string supplierSku, string description, int quantity,
        decimal unitPrice, decimal? listUnitPrice, string currency, string priceExplanation)
    {
        if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (unitPrice < 0) throw new ArgumentOutOfRangeException(nameof(unitPrice));
        ProductId = productId;
        OfferId = offerId;
        SupplierSku = supplierSku;
        Description = description.Length > 256 ? description[..256] : description;
        Quantity = quantity;
        UnitPrice = unitPrice;
        ListUnitPrice = listUnitPrice;
        Currency = currency;
        PriceExplanation = priceExplanation.Length > 200 ? priceExplanation[..200] : priceExplanation;
        LineTotal = Math.Round(unitPrice * quantity, 2);
    }
}

public enum OrderPaymentStatus { Unpaid, Pending, Paid, Refunded }

public enum PaymentProvider { Simulated, Stripe }

public enum PaymentAttemptStatus { Pending, Succeeded, Failed, Cancelled, Refunded }

/// <summary>
/// One attempt to pay a purchase order through a gateway. The amount is copied from the order on the
/// server when the attempt starts, so the client never sets what is charged. At most one attempt per
/// order can succeed (filtered unique index). <see cref="PaymentProvider.Simulated"/> attempts move no
/// money: they exist to exercise the flow locally.
/// </summary>
public sealed class Payment
{
    public long Id { get; private set; }
    public long PurchaseOrderId { get; private set; }
    public PurchaseOrder? PurchaseOrder { get; private set; }
    public PaymentProvider Provider { get; private set; }
    public PaymentAttemptStatus Status { get; private set; } = PaymentAttemptStatus.Pending;
    public bool IsTest { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "GBP";
    /// <summary>Gateway checkout reference (Stripe Checkout Session id, sim_...).</summary>
    public string? CheckoutReference { get; private set; }
    public string? CheckoutUrl { get; private set; }
    /// <summary>Gateway payment reference once paid (Stripe PaymentIntent id); refunds go against it.</summary>
    public string? PaymentReference { get; private set; }
    public string? RefundReference { get; private set; }
    public string? FailureReason { get; private set; }
    public string CreatedBy { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime? RefundedAtUtc { get; private set; }

    private Payment() { }

    public Payment(PurchaseOrder order, PaymentProvider provider, bool isTest, string createdBy)
    {
        if (!order.CanBePaid) throw new InvalidOperationException($"Order {order.Number} cannot be paid ({order.Status}, {order.PaymentStatus}).");
        PurchaseOrder = order;
        PurchaseOrderId = order.Id;
        Provider = provider;
        IsTest = isTest;
        Amount = order.Subtotal;
        Currency = order.Currency;
        CreatedBy = createdBy;
    }

    public void AttachCheckout(string reference, string? url)
    {
        CheckoutReference = reference;
        CheckoutUrl = url;
    }

    /// <summary>Returns false when already applied (webhooks and status syncs can race and repeat).</summary>
    public bool Succeed(string? paymentReference, DateTime now)
    {
        if (Status == PaymentAttemptStatus.Succeeded) return false;
        if (Status != PaymentAttemptStatus.Pending) throw new InvalidOperationException($"A {Status.ToString().ToLowerInvariant()} payment cannot succeed.");
        Status = PaymentAttemptStatus.Succeeded;
        PaymentReference = paymentReference ?? PaymentReference;
        CompletedAtUtc = now;
        FailureReason = null;
        return true;
    }

    public bool Fail(string reason, DateTime now) => Close(PaymentAttemptStatus.Failed, reason, now);
    public bool Cancel(string reason, DateTime now) => Close(PaymentAttemptStatus.Cancelled, reason, now);

    private bool Close(PaymentAttemptStatus status, string reason, DateTime now)
    {
        if (Status != PaymentAttemptStatus.Pending) return false;
        Status = status;
        FailureReason = reason.Length > 300 ? reason[..300] : reason;
        CompletedAtUtc = now;
        return true;
    }

    public void Refunded(string refundReference, DateTime now)
    {
        if (Status != PaymentAttemptStatus.Succeeded) throw new InvalidOperationException("Only a successful payment can be refunded.");
        Status = PaymentAttemptStatus.Refunded;
        RefundReference = refundReference;
        RefundedAtUtc = now;
    }
}
