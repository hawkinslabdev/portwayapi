# ✨ Portway 

A powerful, lightweight API gateway built for Windows Server. You can use it for granular SQL Server data access, and/or service proxying with environment awareness.

![Screenshot of Swagger UI](https://github.com/hawkinslabdev/portwayapi/blob/a96ac4a8aacca879b59a69a370439f994c27b21d/Source/example.png)

## 🚀 Features
- **Multiple Endpoint Types**
  
  We support various endpoint types. You can connect your internal webservices and expose specific endpoints; or you can expose your database directly for specific tables, schema's and/or fields.
  - 🗄️ **SQL API**: Direct SQL Server data access with OData support. This now supports POST requests!
  - 🔀 **Proxy**: Forward requests to internal services with authentication. This now supports chaining, to combine multiple operations in a single request. Call the "Line" endpoint before the "Header", all from one request!
  - 📥 **Webhook**: Process incoming webhooks and store data, directly into your database.
- 🔐 **Secure authentication**: Token-based auth with Azure Key Vault support.
- 🌍 **Environment Awareness** Route to different environments (test, production, etc.).
- 📊 **Automatic documentation**: Swagger UI for all endpoints.
- 📝 **Detailed logging**: Comprehensive request/response tracking. This now supports tracing live data coming in!
- 🔄 **Rate limiting**: Protect services from overload, which is easy to configure.

## 📦 Requirements
- [.NET 8+ ASP.NET Core Runtime](https://dotnet.microsoft.com/en-us/download)
- Internet Information Services (for production)
- SQL Server database access (for SQL endpoints)
- Local write access for logs and configuration

---

## 🛣️ Roadmap

- [ ] Fix the webhook implementation, which now causes a 404.
- [ ] Implement more detailed logging.

---

## 🛠️ Setup

### 1. Download the release
Download the latest release from the releases section and extract it to your desired location.

### 2. Create required folders
These folders will be automatically created when the application runs, but most of them are already available in the set-up package.
```bash
mkdir log
mkdir tokens
mkdir environments
mkdir endpoints/SQL
mkdir endpoints/Proxy
mkdir endpoints/Webhooks
```

### 3. Configure environments
Add a settings file for each environment:

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

### 4. Configure endpoints

#### SQL Endpoint
**`endpoints/SQL/Products/entity.json`**
```json
{
  "DatabaseObjectName": "Items",
  "DatabaseSchema": "dbo",
  "AllowedColumns": [
    "ItemCode","Description","Assortment","sysguid"
  ]
}
```

#### Proxy Endpoint
**`endpoints/Proxy/Accounts/entity.json`**
```json
{ 
  "Url": "http://localhost:8020/services/Exact.Entity.REST.EG/Account", 
  "Methods": ["GET", "POST", "PUT", "DELETE","MERGE"] 
}
```

#### Composite Endpoint
**`endpoints/Proxy/SalesOrder/entity.json`**
```json
{
  "Type": "Composite",
  "Url": "http://localhost:8020/services/Exact.Entity.REST.EG",
  "Methods": ["POST"],
  "CompositeConfig": {
    "Name": "SalesOrder",
    "Description": "Creates a complete sales order with multiple order lines and a header",
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

#### Webhook Endpoint
**`endpoints/Webhooks/entity.json`**
```json
{
  "DatabaseObjectName": "WebhookData",
  "DatabaseSchema": "dbo",
  "AllowedColumns": [
    "webhook1",
    "webhook2"
  ]
}
```

### 5. Run the application

Configure the application as a website in Internet Information Services. Note, if you're going to use the proxy make sure to change the application identity. Make sure to modify your application pool and website settings, for optimal uptime and security policies. E.g. for more information check [Security Headers by Probely](https://securityheaders.com/)

---

## 👮 Secure Authentication
- On first run, a SQLite database `auth.db` will be created with an enhanced security model
- The system automatically generates a token bound to the machine name:
  ```text
  🗝️ Generated token for SERVER-1: <your-token>
  💾 Token saved to: /tokens/SERVER-1.txt
  ```
- Include the token in requests as:
  ```http
  Authorization: Bearer YOUR_TOKEN
  ```

## 🗝️ Azure Key Vault Integration
Set the KEYVAULT_URI environment variable to your Key Vault's URI, and create secrets following the {environment}-ConnectionString and {environment}-ServerName naming convention.

```powershell
$env:KEYVAULT_URI = "https://your-keyvault-name.vault.azure.net/"
```

---

## 🔄 API Usage

### SQL Endpoints
```http
GET /api/prod/Products?$filter=Assortment eq 'Books'&$select=ItemCode,Description
```

### Proxy Endpoints
```http
GET /api/prod/Accounts
POST /api/prod/Accounts
```

### Composite Endpoints
```http
POST /api/prod/composite/SalesOrder
Content-Type: application/json

{
  "Header": {
    "OrderDebtor": "prod93",
    "YourReference": "Connect async"
  },
  "Lines": [
    {
      "Itemcode": "BEK0001",
      "Quantity": 2,
      "Price": 0
    },
    {
      "Itemcode": "BEK0002",
      "Quantity": 4,
      "Price": 0
    }
  ]
}
```

### Webhook Endpoints
```http
POST /webhook/prod/webhook1
Content-Type: application/json

{
  "eventType": "order.created",
  "data": {
    "orderId": "12345",
    "customer": "ACME Corp"
  }
}
```

## 📅 Logging
- Logs are stored in the `/log` folder and rotate daily.
- Console output includes timestamps.
- EF Core database commands are logged at `Warning` level to avoid verbosity.
- Authentication events are logged for auditing purposes.

## 🔒 Security Model
The authentication system implements industry best practices:
- No plaintext tokens stored in the database
- Cryptographically secure hashing with PBKDF2/SHA256
- Username binding for token ownership and auditing
- File-based token distribution for token distribution

## 🔍 Monitoring
Health check endpoints are available at:
```
GET /health
GET /health/live
GET /health/details
```

## ✨ Credits
Built with ❤️ using:
- [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/)
- [DynamicODataToSQL](https://github.com/your-org/dynamicodata-to-sql)
- [Serilog](https://serilog.net/)
- [SQLite](https://www.sqlite.org/index.html)

Feel free to submit a PR if you'd like to contribute.
