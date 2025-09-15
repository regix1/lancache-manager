# Privacy Policy - LanCache Manager

Last Updated: January 2025

## Overview

LanCache Manager respects your privacy. This document explains what data we collect (if any), how we use it, and your control over it.

## Telemetry & Analytics

### What is Telemetry?

Telemetry is **optional** anonymous usage statistics that help us improve the application. It is **disabled by default** and requires explicit opt-in.

### Enabling Telemetry

To enable telemetry, set the following in your `docker-compose.yml`:

```yaml
environment:
  - TELEMETRY_ENABLED=true
```

### What We Collect (When Enabled)

When telemetry is enabled, we collect:

**Anonymous Usage Data:**
- Feature usage (which tabs/features are used)
- Performance metrics (page load times, API response times)
- Error reports (application errors, not your data)
- Browser/OS type (for compatibility)
- Application version

**We NEVER Collect:**
- ❌ Personal information
- ❌ IP addresses
- ❌ Email addresses
- ❌ Cache content or file names
- ❌ Network information
- ❌ Client hostnames or identifiers
- ❌ Any data from your cache logs
- ❌ Downloaded game titles or content

### How We Use This Data

- **Improve Features**: Understand which features are most used
- **Fix Bugs**: Identify common errors and issues
- **Performance**: Optimize slow areas of the application
- **Compatibility**: Ensure support for different environments

### Data Storage

- Anonymous data is stored for up to 90 days
- Data is transmitted securely over HTTPS
- We use industry-standard analytics services (PostHog/Plausible)
- You can self-host analytics if preferred

## Your Cache Data

**We have no access to your cache data.** LanCache Manager runs entirely on your infrastructure:

- All data stays on your server
- No cloud connectivity required
- No data leaves your network (unless telemetry is explicitly enabled)
- The application works 100% offline

## Third-Party Services

The application may connect to:

- **Steam API**: To fetch game information (names, images)
  - Only when viewing Steam downloads
  - No personal data sent
  - Only game IDs are queried

- **GitHub**: For update checks (optional)
  - Can be disabled
  - No personal data sent

## Data Control

You have complete control:

1. **Telemetry is opt-in**: Disabled by default
2. **Run offline**: The app works without internet
3. **Self-hosted**: All data stays on your server
4. **Open source**: Audit the code yourself

## Security

- No authentication data is stored (API keys are memory-only)
- Mock mode available for testing without real data
- All telemetry data is anonymized before transmission
- HTTPS only for any external communications

## Children's Privacy

This application is not intended for children under 13. We do not knowingly collect data from children.

## Changes to This Policy

We may update this policy. Changes will be noted in the changelog and this document's "Last Updated" date.

## Contact

For privacy concerns or questions:

- Open an issue on GitHub
- Email: realregix@proton.me

## Compliance

This application is designed to be compliant with:

- GDPR (General Data Protection Regulation)
- CCPA (California Consumer Privacy Act)
- Privacy by default principles

## Summary

- **Telemetry is OFF by default**
- **You must explicitly enable it**
- **No personal data is ever collected**
- **All your cache data stays on your server**
- **You can run 100% offline**

---

By using LanCache Manager with telemetry enabled, you agree to this privacy policy.