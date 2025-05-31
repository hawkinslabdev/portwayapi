# Licensing

Portway offers flexible licensing to suit different use cases, from open source projects to enterprise deployments.

## License Types

### Open Source (AGPL-3.0)
- **Free forever** for open source projects
- Full core functionality included
- Community support via GitHub
- Source code modifications must be shared under AGPL

### Commercial License
- **Professional** — €19/month for growing businesses
- **Enterprise** — Custom pricing for large organizations
- Allows usage of multiple API-keys
- Priority support and guaranteed patches
- DTAP environment configurations included

## Getting a License

1. Visit [melosso.com/licensing/portway](https://melosso.com/licensing/portway)
2. Choose your plan and complete purchase
3. Access your license key from the [account portal](https://melosso.com/portal)
4. Follow the activation steps below

## License Activation

### Step 1: Locate Your License Key

After purchasing, you'll receive a license key in the format:
```
LIC-1234567890-ABC123
```

Find your key in:
- Email confirmation
- [Account portal](https://melosso.com/portal) → Licenses

### Step 2: Activate the License

#### Option A: Automatic Activation (Recommended)

1. **Start Portway** with internet connectivity
2. **Navigate to the license activation page** in your browser:
   ```
   https://your-portway-server/license/activate
   ```
3. **Enter your license key** and click "Activate"
4. **Verify activation** — you should see "Professional" features enabled

#### Option B: Manual Activation (Offline)

If your server doesn't have internet access:

1. **Generate machine ID** by visiting:
   ```
   https://your-portway-server/license/machine-id
   ```
2. **Submit offline activation request** at [melosso.com/portal](https://melosso.com/portal)
3. **Download the license file** and place it in:
   ```
   /path/to/portway/license.portway
   ```
4. **Restart Portway** to load the license

### Step 3: Verify License Status

Check your license status at any time:
```
https://your-portway-server/license/status
```

Or via API:
```bash
curl -H "Authorization: Bearer YOUR_TOKEN" \
     https://your-portway-server/api/license/status
```

## Professional Features

With an active commercial license, you gain access to:

### ✅ Guaranteed Patches
- Critical security updates within 24 hours
- Bug fixes prioritized for licensed users
- Compatibility updates for new Windows Server versions

### ✅ DTAP Environment Support
```json
{
  "AllowedEnvironments": ["dev", "test", "acceptance", "prod"],
  "EnvironmentIsolation": true,
  "CrossEnvironmentDeployment": true
}
```

### ✅ Priority Support
- Direct email support with SLA
- Faster response times for technical issues
- Access to advanced configuration guidance

### ✅ Extended Endpoint Limits
- Unlimited SQL endpoints (vs 10 in open source)
- Advanced composite endpoint patterns
- Enhanced caching and rate limiting options

## License Management

### Transferring Licenses
Licenses are machine-bound for security. To transfer:

1. **Deactivate** on the old server:
   ```
   https://old-server/license/deactivate
   ```
2. **Activate** on the new server using the same license key

### Monitoring Usage
Track your license usage in the [account portal](https://melosso.com/portal):
- Active installations
- License expiration dates
- Usage statistics and logs

### Renewal
Licenses auto-renew by default. Manage renewal settings in your account portal.

## Troubleshooting

### License Not Recognized
```bash
# Check license file permissions
ls -la license.portway

# Verify file contents
cat license.portway | head -5
```

### Activation Failures
1. **Check internet connectivity** if using automatic activation
2. **Verify license key format** (should start with `LIC-`)
3. **Ensure license isn't already activated** on another machine
4. **Contact support** if issues persist

### Common Error Messages

| Error | Solution |
|-------|----------|
| "License key invalid" | Double-check the key format and try again |
| "License already activated" | Deactivate from previous machine first |
| "Network connection failed" | Use manual activation for offline servers |
| "License expired" | Renew your subscription in the portal |

## Support

Need help with licensing?

- **Documentation**: This guide and our [FAQ](../troubleshooting#licensing)
- **Community**: [GitHub Discussions](https://github.com/melosso/portway/discussions) for general questions
- **Commercial Support**: Email support for licensed users
- **Enterprise**: Direct contact for custom licensing needs

---

*For enterprise licensing, bulk discounts, or custom terms, [contact our sales team](https://melosso.com/contact).*