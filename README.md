# <img src="https://github.com/melosso/portway/blob/main/Source/logo.webp?raw=true" alt="" width="34" style="vertical-align: middle;">  Portway

**Portway** is a fast, lightweight API gateway optimized for Windows Server environments. It offers fine-grained access to SQL Server data and flexible service proxying — with full environment-awareness, secure auth, and automatic documentation.

> 📍 [Landing Page](https://portway.melosso.com/) | 🧪 [Live Demo](https://portway-demo.melosso.com/)

A quick example to give you an idea of what this is all about:

![Screenshot of Swagger UI](https://github.com/melosso/portway/blob/main/Source/example.png?raw=true)

## 🧩 Key Features

Portway is built with flexibility and control in mind. Whether you're proxying services or exposing SQL endpoints, Portway adapts to your infrastructure with secure, high-performance routing.

* **Multiple endpoint types**:

  * **SQL Server (OData)** — direct CRUD access with schema-level control
  * **Proxy** — forward to internal services; supports complex orchestration
  * **File System** — read/write from local storage or cache
  * **Webhook** — receive external calls and persist data to SQL
* **Auth system**: Token-based, with Azure Key Vault integration
* **Environment-aware routing**: Dev, staging, production — all isolated and configurable
* **Built-in Swagger**: Every endpoint is documented out-of-the-box
* **Comprehensive logging**: Request/response tracing, including live monitoring
* **Rate limiting**: Easy to configure; protects downstream systems

## ⚙️ Requirements

Before deploying Portway, make sure your environment meets the following requirements. These ensure full functionality across all features, especially SQL and authentication.

* [.NET 9+ Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
* IIS (production hosting) if hosting on Windows
* SQL Server access (for SQL endpoints)
* Local filesystem access for configuring/running the application

Ready to go? Then continue:

## 🚀 Getting Started

Follow these steps to get Portway up and running in your environment. Setup is fast and modular, making it easy to configure just what you need.

### 1. Download & Extract

Grab the [latest release](https://github.com/melosso/portway/releases) and extract it to your deployment folder.

### 2. Configure Your Environments

Define your server and environment settings to isolate dev/staging/prod as needed. These configs are used across endpoints and logging.

**`environments/settings.json`**

```json
{
  "Environment": {
    "ServerName": "localhost",
    "AllowedEnvironments": ["prod", "dev"]
  }
}
```

**`environments/prod/settings.json`**

```json
{
  "ServerName": "localhost",
  "ConnectionString": "Server=localhost;Database=prod;Trusted_Connection=True;Connection Timeout=5;TrustServerCertificate=true;"
}
```

### 3. Define Your Endpoints

Endpoints are configured as JSON files. Each type has its own directory and format, making them easy to manage and extend.

#### SQL Endpoint — `endpoints/SQL/Products/entity.json`

Exposes a SQL table with restricted columns and CRUD operations.

```json
{
  "DatabaseObjectName": "Items",
  "DatabaseSchema": "dbo",
  "PrimaryKey": "ItemCode",
  "AllowedColumns": ["ItemCode", "Description", "Assortment", "sysguid"],
  "AllowedEnvironments": ["prod", "dev"]
}
```

#### Proxy Endpoint — `endpoints/Proxy/Accounts/entity.json`

Acts as a reverse proxy for internal services with full method control.

```json
{
  "Url": "http://localhost:8020/services/Exact.Entity.REST.EG/Account",
  "Methods": ["GET", "POST", "PUT", "DELETE", "MERGE"],
  "AllowedEnvironments": ["prod", "dev"]
}
```

#### Composite Endpoint — `endpoints/Proxy/SalesOrder/entity.json`

Combines multiple calls into a single logical transaction for APIs requiring sequential or nested operations.

```json
{
  "Type": "Composite",
  "Url": "http://localhost:8020/services/Exact.Entity.REST.EG",
  "Methods": ["POST"],
  "CompositeConfig": {
    "Name": "SalesOrder",
    "Description": "Creates a complete sales order with multiple lines and header",
    "Steps": [
      {
        "Name": "CreateOrderLines",
        "Endpoint": "SalesOrderLine",
        "Method": "POST",
        "IsArray": true,
        "ArrayProperty": "Lines",
        "TemplateTransformations": {
          "TransactionKey": "$guid"
        }
      },
      {
        "Name": "CreateOrderHeader",
        "Endpoint": "SalesOrderHeader",
        "Method": "POST",
        "SourceProperty": "Header",
        "TemplateTransformations": {
          "TransactionKey": "$prev.CreateOrderLines.0.d.TransactionKey"
        }
      }
    ]
  }
}
```

#### Webhook Endpoint — `endpoints/Webhooks/entity.json`

Used for receiving and storing webhook payloads directly into your database.

```json
{
  "DatabaseObjectName": "WebhookData",
  "DatabaseSchema": "dbo",
  "AllowedColumns": ["webhook1", "webhook2"]
}
```

### 4. Deploy

Host the app in IIS. For proxy usage, configure the correct identity. Ensure your app pool and security settings are production-ready. Note, if you're going to use the proxy make sure to change the application identity. Make sure to modify your application pool and website settings, for optimal uptime and security policies. E.g. for more information check [Security Headers by Probely](https://securityheaders.com/)

## 🔐 Auth & Security

### Token-Based Auth (Local)

Portway uses a lightweight token-based system for authentication. Tokens are machine-bound and stored securely on disk.

```bash
🗝️ Generated token for SERVER-1: <your-token>
💾 Saved to /tokens/SERVER-1.txt
```

Include the token in request headers:

```http
Authorization: Bearer YOUR_TOKEN
```

### Azure Key Vault Support

To centralize and secure configuration secrets, use Azure Key Vault. Portway can read secrets automatically by environment.

```powershell
$env:KEYVAULT_URI = "https://your-keyvault-name.vault.azure.net/"
```

Secrets format: `{env}-ConnectionString` and `{env}-ServerName`

## 📡 API Examples

Here are some common requests you'll make using Portway's endpoints.

### SQL

Query specific data with full OData support:

```http
GET /api/prod/Products?$filter=Assortment eq 'Books'&$select=ItemCode,Description
```

### Proxy

Forward calls to internal REST services:

```http
GET /api/prod/Accounts
POST /api/prod/Accounts
```

### Composite

Chain together multiple operations into one:

```http
POST /api/prod/composite/SalesOrder
Content-Type: application/json
{
  "Header": {
    "OrderDebtor": "60093",
    "YourReference": "Connect async"
  },
  "Lines": [
    { "Itemcode": "BEK0001", "Quantity": 2, "Price": 0 },
    { "Itemcode": "BEK0002", "Quantity": 4, "Price": 0 }
  ]
}
```

### Webhooks

Receive data from external services:

```http
POST /api/prod/webhook/webhook1
Content-Type: application/json
{
  "eventType": "order.created",
  "data": {
    "orderId": "12345",
    "customer": "ACME Corp"
  }
}
```

## 📊 Logging & Monitoring

Portway provides visibility into its operations with detailed logs and health check endpoints.

* Logs stored under `/log` with daily rotation
* Auth logs included for auditing
* Health endpoints:

  ```http
  GET /health
  GET /health/live
  GET /health/details
  ```

## 🤝 Credits

Thanks to the open source tools that make Portway possible:

* [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/)
* [DynamicODataToSQL](https://github.com/DynamicODataToSQL/DynamicODataToSQL)
* [Serilog](https://serilog.net/)
* [SQLite](https://www.sqlite.org/index.html)

## License

Portway is available under two licensing models:

* **Open Source (AGPL-3.0)** — Free for open source projects and personal use
* **Commercial License** — For commercial use with full transparency of the open source project

**Professional features** (multiple API-keys, priority support, guaranteed patches, DTAP environments) require a [commercial license](https://melosso.com/licensing/portway). Activation is simple with a license key from your account portal.

[Get your license →](https://melosso.com/licensing/portway)

## Contribution 

Contributions welcome — submit a PR if you'd like to help improve Portway. With your contributions, you’ll be eligible for a free license. 
