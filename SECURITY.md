# Security policy

GCS File Viewer runs on your own machine with your own Google Cloud credentials, so a flaw in it could expose or damage data in your buckets. Reports are welcome.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use GitHub's private reporting instead: on the repository's [Security tab](https://github.com/hcornelison/gcs-file-viewer/security), choose **Report a vulnerability**. Only the maintainer sees the report.

Include what you did, what happened, and the app version or commit. Leave out real credentials, access tokens and bucket contents.

This is a volunteer project with no guaranteed response time, but reports are read and a fix or an explanation will follow.

## Supported versions

Only the latest release and the `main` branch receive fixes.

## How the app handles credentials

- It never stores credentials. It uses a key file you point it at, Application Default Credentials, or an access token it asks the gcloud CLI for, and keeps them in memory only.
- It talks only to Google's Cloud Storage and Resource Manager APIs. It has no telemetry and no update check.
- The only file it writes outside your chosen download folders is its settings file, which holds the light or dark mode choice.
