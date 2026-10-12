import '../platform/platform_info.dart';

class AppConfig {
  const AppConfig({required this.apiBaseUri});

  static const _buildApiBaseUrl = String.fromEnvironment('TKG_API_BASE_URL');
  static const _androidDevelopmentApi = 'http://10.0.2.2:5000';

  final Uri apiBaseUri;

  static AppConfig resolve({
    required PlatformInfo platformInfo,
    String? runtimeApiBaseUrl,
    String buildApiBaseUrl = _buildApiBaseUrl,
  }) {
    final configured = buildApiBaseUrl.trim().isNotEmpty
        ? buildApiBaseUrl.trim()
        : runtimeApiBaseUrl?.trim().isNotEmpty == true
        ? runtimeApiBaseUrl!.trim()
        : platformInfo.isWeb
        ? (platformInfo.browserOrigin != null &&
                  platformInfo.browserOrigin!.hasPort &&
                  platformInfo.browserOrigin!.port == 7357
              ? '${platformInfo.browserOrigin!.scheme}://${platformInfo.browserOrigin!.host}:5000'
              : platformInfo.browserOrigin?.toString() ?? '')
        : _androidDevelopmentApi;
    final uri = Uri.tryParse(configured);
    if (uri == null ||
        !{'http', 'https'}.contains(uri.scheme.toLowerCase()) ||
        uri.host.isEmpty ||
        uri.userInfo.isNotEmpty ||
        uri.query.isNotEmpty ||
        uri.fragment.isNotEmpty) {
      throw StateError(
        'TKG API adresi user-info/query/fragment içermeyen geçerli bir HTTP(S) adresi olmalıdır.',
      );
    }
    return AppConfig(apiBaseUri: uri);
  }
}
