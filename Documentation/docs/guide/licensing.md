# Licensing

Portway offers flexible licensing options to suit different use cases, from personal projects to enterprise deployments.

## License Types

### Free Tier
- **Free forever** for basic usage
- Core API gateway functionality
- Community support via GitHub
- Limited to basic features

### Professional Tier
- **Paid subscription** for commercial use
- All features unlocked including advanced capabilities
- Commercial usage permitted
- Priority support and updates
- Redis caching, traffic logging, and composite endpoints

## Getting a License

1. Visit the Portway licensing portal
2. Choose the Professional tier
3. Complete the purchase process
4. Receive your license key via email
5. Follow the activation steps below

## License Activation

### Method 1: Direct License File (Recommended)

The simplest way to activate your license:

1. **Locate License Key** - From your purchase confirmation email
2. **Create License File** - Create a new text file named `.license` (note the dot at the beginning)
3. **Save License Content** - Copy your license key into this file
4. **Place in Portway Directory** - Put the `.license` file in the same folder as your Portway application
5. **Restart Portway** - Restart the application or recycle the application pool (IIS)

**Example `.license` file content:**
```
LIC-1734567890-ABC123
```

**File location examples:**
- **IIS**: `C:\inetpub\wwwroot\YourPortwayApp\.license`
- **Standalone**: `C:\Portway\.license`
- **Custom Path**: Same directory as `PortwayApi.exe`

### Method 2: API Activation

For programmatic activation, you can use the licensing API:

**POST** to `/api/license/activate`
```json
{
  "licenseKey": "LIC-1734567890-ABC123"
}
```

**Headers required:**
```
Content-Type: application/json
Authorization: Bearer YOUR_API_TOKEN
```

### Verifying Activation

After placing the license file and restarting:

1. **Check Application Logs** - Look for license status messages during startup
2. **Test Professional Features** - Try accessing features like Redis caching or composite endpoints
3. **Monitor License Status** - Check if restrictions are lifted

**Successful activation log messages:**
```
🔐 License service initialized
📋 Valid signed license loaded: Professional
✅ License activated and verified successfully
```

## License File Format

After successful activation, Portway automatically creates a detailed `.license` file:

```json
{
  "licenseKey": "LIC-1734567890-ABC123",
  "productId": "portway-pro",
  "status": "active",
  "tier": "professional",
  "expiresAt": "2025-12-31T23:59:59Z",
  "activatedAt": "2024-12-01T10:30:00Z",
  "machineId": "abc123def456",
  "signature": "...",
  "features": ["redis-caching", "traffic-logging", "composite-endpoints"]
}
```

## Managing Your License

### Checking License Status

**Method 1: Application Logs**
Check the Portway application logs during startup for license information.

**Method 2: Feature Testing**
Try to use Professional features:
- Access Redis caching configuration
- Create composite endpoints
- Enable traffic logging

### Transferring Licenses

Professional licenses can be moved between installations:

1. **Stop Portway** on the current server
2. **Delete `.license` file** from the current installation
3. **Copy license key** to the new installation following the activation steps above
4. **Restart Portway** on the new server

### License Issues

**License Not Found:**
- Ensure the `.license` file is in the correct directory
- Check file permissions (application must be able to read the file)
- Verify the file name starts with a dot (`.license`)

**Invalid License:**
- Check for extra spaces or characters in the license file
- Ensure the license key format is correct
- Verify the license is for the correct product

**License Expired:**
- Contact support for renewal options
- Check if auto-renewal is enabled in your account

## Features by Tier

| Feature | Free | Professional |
|---------|------|--------------|
| Core API Gateway | ✅ | ✅ |
| SQL Endpoints | ✅ | ✅ |
| Proxy Endpoints | ✅ | ✅ |
| File Storage | ✅ | ✅ |
| Memory Caching | ✅ | ✅ |
| Authentication & Rate Limiting | ✅ | ✅ |
| **Multiple Authenication Keys** | Limited | Unlimited |
| **Commercial Use** | ❌ | ✅ |
| **Priority Support** | ❌ | ✅ |

## Troubleshooting

### Common File Issues

**License file not found:**
```
- Check file location: same directory as Portway executable
- Verify file name: exactly ".license" (with dot)
- Confirm file encoding: plain text (UTF-8)
- Test file permissions: application can read the file
```

**License file ignored:**
```
- Restart Portway completely
- Check application logs for license errors
- Verify file contains only the license key
- Ensure no extra characters or line breaks
```

### Getting Support

**Community Support:**
- GitHub issues for technical problems
- Documentation and FAQ sections
- Community forums and discussions

**Professional Support:**
- Email support with license details
- Priority response for licensed customers
- Direct technical assistance

**License Support:**
- License activation assistance
- Transfer and renewal help
- Custom licensing arrangements

---

*For licensing questions, technical support, or custom arrangements, contact support through the official channels provided with your license purchase.*