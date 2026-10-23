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

## Finished-goods notifications

Warehouse tablets that can receive finished goods keep a native Android foreground service after login. The service long-polls the TKG API with a dedicated credential stored in the Android Keystore. The field-session lease is not used for that channel. Explicit logout revokes the registration and stops the service. Web/PWA does not start the service; it keeps the existing in-app refresh. No external notification vendor is used.
