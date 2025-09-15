# Developer Telemetry Setup Guide

This guide explains how to set up analytics to see how your application is being used.

## Quick Start Options

### Option 1: PostHog (Recommended - Free tier available)

1. **Sign up at [PostHog.com](https://posthog.com)**
   - Free tier includes 1M events/month
   - No credit card required

2. **Get your API key:**
   - Go to Project Settings → API Keys
   - Copy your Project API Key

3. **Update your environment:**
   ```bash
   # In your .env file or docker-compose.yml
   VITE_POSTHOG_API_KEY=phc_YOUR_KEY_HERE
   VITE_POSTHOG_HOST=https://app.posthog.com  # or your self-hosted URL
   ```

4. **View your dashboard:**
   - PostHog provides automatic dashboards for:
     - Active users
     - Page views
     - Feature usage
     - Error tracking
     - User paths
     - Retention

### Option 2: Plausible Analytics (Privacy-focused)

1. **Sign up at [Plausible.io](https://plausible.io)**
   - 30-day free trial
   - Very privacy-focused (no cookies)

2. **Add your domain**

3. **Update telemetry service** to send events to Plausible

### Option 3: Self-Hosted Analytics

#### Using Umami (Simple & Open Source)

```yaml
# docker-compose.yml for Umami
version: '3'
services:
  umami:
    image: ghcr.io/umami-software/umami:postgresql-latest
    ports:
      - "3000:3000"
    environment:
      DATABASE_URL: postgresql://umami:umami@db:5432/umami
      DATABASE_TYPE: postgresql
      HASH_SALT: replace-me-with-random-string
    depends_on:
      - db

  db:
    image: postgres:15-alpine
    environment:
      POSTGRES_DB: umami
      POSTGRES_USER: umami
      POSTGRES_PASSWORD: umami
    volumes:
      - umami-db:/var/lib/postgresql/data

volumes:
  umami-db:
```

## What You'll See in Analytics

### User Metrics
- **Daily/Weekly/Monthly Active Users**: How many people use your app
- **Geographic Distribution**: Where your users are (country-level)
- **New vs Returning**: User retention rates

### Feature Usage
- **Most Used Features**: Which tabs/features are popular
- **Feature Adoption**: How many users try new features
- **User Flows**: Common paths through your app

### Performance & Errors
- **Page Load Times**: Which pages are slow
- **API Performance**: Slow endpoints
- **Error Reports**: Common errors users encounter
- **Browser/OS Stats**: What platforms to support

### Example PostHog Dashboard

After setup, you'll see:

```
Active Users: 234
├── Daily: 45
├── Weekly: 156
└── Monthly: 234

Top Features (Last 30 days):
1. Downloads Tab - 89% of users
2. Dashboard - 76% of users
3. Management Tab - 45% of users
4. Theme Customization - 23% of users

Common Errors:
1. "Failed to fetch game info" - 12 occurrences
2. "API timeout" - 8 occurrences

User Retention:
Week 1: 78%
Week 2: 45%
Week 4: 32%
```

## Setting Up Error Tracking with Sentry

1. **Sign up at [Sentry.io](https://sentry.io)**
   - Free tier: 5K errors/month

2. **Create a project** (choose React)

3. **Add to your app:**
   ```typescript
   // In telemetry.service.ts
   import * as Sentry from "@sentry/react";

   Sentry.init({
     dsn: "YOUR_SENTRY_DSN",
     environment: "production",
     beforeSend(event) {
       // Remove any PII
       return event;
     }
   });
   ```

## GitHub Analytics Integration

### Using GitHub Insights

1. **GitHub Stars History**: Track growth
   - Use [Star History](https://star-history.com)

2. **GitHub Traffic**: In your repo → Insights → Traffic
   - Shows clones, visits, popular content

3. **GitHub Releases**: Track downloads
   - Each release shows download counts

### Community Health Metrics

Create a dashboard showing:
- Stars over time
- Issues opened/closed
- PR merge rate
- Community contributors

## Privacy Compliance Checklist

- [ ] Telemetry is OPT-IN by default
- [ ] Privacy policy is clear and accessible
- [ ] No PII is collected
- [ ] Users can disable telemetry easily
- [ ] Data retention is limited (90 days recommended)
- [ ] GDPR compliant (anonymous data only)

## Sample Analytics Code

```typescript
// Track feature usage
telemetryService.track('feature_used', {
  feature: 'downloads_tab',
  action: 'filter_applied',
  filter_type: 'service'
});

// Track performance
telemetryService.track('performance', {
  page: 'dashboard',
  load_time_ms: 234,
  api_calls: 3
});

// Track errors (automatically sanitized)
window.addEventListener('error', (event) => {
  telemetryService.trackError(event.error, {
    page: window.location.pathname
  });
});
```

## Viewing Your Analytics

### PostHog Dashboard
1. Log into PostHog
2. Default dashboards show everything
3. Create custom dashboards for:
   - Feature adoption
   - Error tracking
   - Performance metrics

### Custom Reporting
Export data to:
- Google Sheets (via Zapier)
- Grafana (via API)
- Custom dashboards (via API)

## Cost Estimates

- **PostHog Free**: Up to 1M events/month
- **Plausible**: $9/month for 10K pageviews
- **Sentry Free**: 5K errors/month
- **Self-hosted**: Only your server costs

## Best Practices

1. **Start Simple**: Just track page views and errors initially
2. **Add Gradually**: Add more events as you need them
3. **Review Weekly**: Check your dashboard weekly
4. **Act on Data**: Use insights to prioritize features
5. **Respect Privacy**: Never track PII

## Support

If users report telemetry issues:
1. Check if they have `TELEMETRY_ENABLED=true`
2. Verify no firewall blocking
3. Check browser console for errors
4. Telemetry is non-critical (app works without it)