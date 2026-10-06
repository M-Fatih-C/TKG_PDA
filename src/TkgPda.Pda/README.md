# TKG Operator Flutter Client

Single-codebase Android industrial PDA and Windows Web/PWA client for TKG logistics operations. The backend is authoritative for permissions, slot eligibility, available transport actions and execution status. No native Windows client exists.

## Local validation

```powershell
flutter pub get
dart format --output=none --set-exit-if-changed .
flutter analyze
flutter test
flutter build apk --debug --dart-define=TKG_API_BASE_URL=https://api.example.invalid
flutter build web --release --no-web-resources-cdn --base-href /operator/ --output build/operator
```

For local Web development:

```powershell
flutter run -d chrome --web-hostname=127.0.0.1 --web-port=7357 `
  --dart-define=TKG_API_BASE_URL=http://127.0.0.1:5000
```

For Android emulator access to a host API, the development default is `http://10.0.2.2:5000`. Production should use HTTPS. Build define takes precedence; Web may alternatively use non-secret `web/operator-config.json`, then safely falls back to same-origin. JWT and password are never persisted. The provisioned Device Code is stored in Android secure storage or Web origin-local storage under the sole key `tkg.pda.device-code`.

See [`docs/PHASE_09_MULTIPLATFORM_OPERATOR.md`](../../docs/PHASE_09_MULTIPLATFORM_OPERATOR.md) and [`docs/OPERATOR_WEB_DEPLOYMENT.md`](../../docs/OPERATOR_WEB_DEPLOYMENT.md) for architecture, security boundaries, browser acceptance and deployment.

## Android finished-goods push

Web/PWA keeps the existing in-app polling. Android push is optional and is not initialized unless all four build defines are set. Do not commit `google-services.json`, a service-account file, or real values.

```powershell
flutter build apk --release `
  --dart-define=TKG_API_BASE_URL=https://api.example.invalid `
  --dart-define=TKG_FIREBASE_API_KEY=... `
  --dart-define=TKG_FIREBASE_APP_ID=... `
  --dart-define=TKG_FIREBASE_MESSAGING_SENDER_ID=... `
  --dart-define=TKG_FIREBASE_PROJECT_ID=...
```

The API sends only after a finished container is actually stored. Enable it with `PushNotifications:Enabled=true`, `PushNotifications:Provider=Firebase`, and `PushNotifications:FirebaseProjectId`. Authentication is Application Default Credentials (`GOOGLE_APPLICATION_CREDENTIALS` or the host equivalent). If push is enabled and that configuration is missing, the API fails at startup. With push disabled, warehouse polling and in-app notifications continue.
