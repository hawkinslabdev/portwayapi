# 🌐 Portway (Remote gateway API)

Portway is a powerful and flexible API integration solution, combining the capabilities of proxy forwarding and direct SQL endpoint access. This project is a combination of two original projects: MinimalProxy and MinimalSQLAPI, merged into a unified solution.

## ✨ Key Features

-  **Endpoint Types**
    You can configure different types of API endpoints, each designed for specific use cases:

    - **SQL API Endpoints**  
    Automatically generate endpoints from SQL tables or stored procedures.

    - **Proxy Endpoints**  
    Forward requests to backend services, with built-in support for URL rewriting and authentication.

    - **Composite Endpoints**  
    Combine multiple API calls into one, including support for data transformation between steps.

    - **Webhook Endpoints**  
    Receive and process incoming webhooks, with the ability to store data directly in a SQL database.

- **Authentication**: Secure token-based authentication with automatic key generation
- **Health Monitoring**: Built-in health checks for system diagnostics
- **Rate Limiting**: Protect your services from overuse with configurable rate limiting
- **OData Support**: Use OData query syntax for filtering, pagination, and sorting

## 🗂️ Project Structure

```
Portway/
├── Api/                    # API controllers  
├── Auth/                   # Authentication handlers
├── Classes/                # Core functionality classes
├── endpoints/              # Endpoint definitions
│   ├── Proxy/              # Proxy endpoint configurations
│   ├── SQL/                # SQL endpoint configurations
│   └── Webhooks/           # Webhook configurations
├── environments/           # Environment configuration
│   ├── 600/                # Environment-specific settings
│   └── 700/                # Environment-specific settings
├── Helpers/                # Utility helper classes
├── Interfaces/             # Interface definitions
├── Middleware/             # Request pipeline middleware
├── Services/               # Background and supporting services
└── wwwroot/                # Static web content
```

## 🚀 Getting Started

### 🛠️ Prerequisites

- .NET 8.0 SDK or later
- SQL Server (for SQL API endpoints)
- Access to the target services (for proxy endpoints)

### 📦 Installation

1. Clone the repository:
   ```
   git clone https://github.com/hawkinslabdev/Portway.git
   ```

2. Navigate to the project directory:
   ```
   cd Portway/Source/Portway
   ```

3. Restore packages and build the solution:
   ```
   dotnet restore
   dotnet build
   ```

4. Run the application:
   ```
   dotnet run
   ```

5. Access the Swagger UI:
   ```
   http://localhost:5171/swagger
   ```

### ⚙️ Configuration

#### 🌍 Environment Configuration

Create environment settings in the `environments/{env}` folder. Each environment folder should contain a `settings.json` file with the following structure:

```json
{
  "Environment": {
    "ServerName": "localhost",
    "AllowedEnvironments": ["dev", "test", "prod"],
    "ConnectionString": "Server=localhost;Database=MyDatabase;User Id=myuser;Password=mypassword;"
  }
}
```

#### 🗄️ SQL Endpoint Configuration

Create a folder in `endpoints/SQL/{EndpointName}` with an `entity.json` file:

```json
{
  "DatabaseObjectName": "Items",
  "DatabaseSchema": "dbo",
  "AllowedColumns": ["ItemCode", "Description", "Price"],
  "AllowedMethods": ["GET", "POST", "PUT", "DELETE"],
  "Procedure": "dbo.HandleItems"
}
```

#### 🔀 Proxy Endpoint Configuration

Create a folder in `endpoints/Proxy/{EndpointName}` with an `entity.json` file:

```json
{
  "Url": "http://backend.service/api/endpoint",
  "Methods": ["GET", "POST", "PUT", "DELETE"],
  "IsPrivate": false
}
```

#### 🧩 Composite Endpoint Configuration

Create a folder in `endpoints/Proxy/{EndpointName}` with an `entity.json` file:

```json
{
  "Type": "Composite",
  "Url": "http://localhost:8020/services/Exact.Entity.REST.EG",
  "Methods": ["POST"],
  "CompositeConfig": {
    "Name": "SampleComposite",
    "Description": "Sample composite endpoint",
    "Steps": [
      {
        "Name": "Step1",
        "Endpoint": "Endpoint1",
        "Method": "POST",
        "TemplateTransformations": {
          "TransactionKey": "$guid"
        }
      },
      {
        "Name": "Step2",
        "Endpoint": "Endpoint2",
        "Method": "POST",
        "DependsOn": "Step1",
        "TemplateTransformations": {
          "TransactionKey": "$prev.Step1.TransactionKey"
        }
      }
    ]
  }
}
```

## 🔒 Authentication

The application uses token-based authentication. On first run, a token will be generated and saved to the `tokens` directory. You must include this token in your requests:

```
Authorization: Bearer {token}
```

## 📡 Using the API

### 🗄️ SQL Endpoints

```
GET /api/{env}/{endpoint}?$select=Column1,Column2&$filter=Column3 eq 'Value'&$top=10&$skip=0
```

### 🔀 Proxy Endpoints

```
GET /api/{env}/{endpoint}/{path}?queryParam=value
```

### 🧩 Composite Endpoints

```
POST /api/{env}/composite/{endpoint}
```

With a JSON body containing the data for all steps.

### 📥 Webhook Endpoints

```
POST /webhook/{env}/{webhookId}
```

With a JSON payload in the request body.

## 🩺 Health Checks

Access system health information:

```
GET /health
GET /health/live
GET /health/details
```

## 🛠️ Troubleshooting

- Check the log files in the `log` directory for detailed error information
- Verify that endpoint configurations are correctly formatted
- Ensure database connections are properly configured in environment settings
- Check authentication tokens are valid and included in requests

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 📜 License

This project is licensed under the MIT License - see the LICENSE file for details.