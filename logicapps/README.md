# Logic Apps (Standard) — local development

These workflows mirror the production "import notification + monitoring" flows and run
**locally** with the Azure Functions Core Tools + Azurite storage emulator.

## Workflows

| Workflow | Trigger | What it does |
|---|---|---|
| `import-notify` | HTTP request | Given `{ "importId": N }`, logs into the Catalogue API, fetches the import status, and returns status/rows/correlation ID. |
| `import-monitor` | Recurrence (every 5 min) | Lists imports, and for every `Failed`/`Rejected` import fetches the row-level errors (in Azure this is where a Teams/email action would go). |

## Run locally

Prereqs: Azure Functions Core Tools v4, Azurite (or Docker compose Azurite service).

```powershell
# Terminal 1 - storage emulator (or use docker compose azurite service)
azurite --silent --location $env:TEMP\azurite

# Terminal 2 - the catalogue API
dotnet run --project src/Catalogue.Web --urls http://localhost:5080

# Terminal 3 - logic apps host
cd logicapps
func host start
```

Then trigger the HTTP workflow:

```powershell
curl.exe -X POST "http://localhost:7071/api/import-notify/triggers/manual/invoke" `
  -H "Content-Type: application/json" `
  -d '{\"importId\": 1}' `
  -w "%{http_code}"
```

(Use `func azure functionapp list-functions logicapps` if the URL differs; the auth code
is disabled locally by default.)

Parameters are set in `local.settings.json` / each workflow's `parameters` section and
default to the local API at `http://localhost:5080` with the dev admin credentials.
In Azure these would come from App Settings / Key Vault references instead.
