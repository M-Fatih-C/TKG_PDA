import 'package:flutter/material.dart';

import '../../app/app_controller.dart';
import '../../shared/widgets/feedback_banners.dart';

class LoginScreen extends StatefulWidget {
  const LoginScreen({required this.controller, super.key});

  final AppController controller;

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _employeeNumber = TextEditingController();

  @override
  void dispose() {
    _employeeNumber.dispose();
    super.dispose();
  }

  Future<void> _login() async {
    if (!_formKey.currentState!.validate() || widget.controller.busy) {
      return;
    }
    FocusScope.of(context).unfocus();
    await widget.controller.login(_employeeNumber.text);
  }

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
    animation: widget.controller,
    builder: (context, _) => Scaffold(
      appBar: AppBar(
        title: const Text('TKG Operatör Girişi'),
        actions: [
          TextButton.icon(
            key: const Key('change-device-code'),
            onPressed: widget.controller.busy ? null : _confirmDeviceChange,
            icon: const Icon(Icons.settings, color: Colors.white70, size: 20),
            label: const Text(
              'Cihazı Değiştir',
              style: TextStyle(color: Colors.white, fontWeight: FontWeight.w600),
            ),
          ),
          const SizedBox(width: 8),
        ],
      ),
      body: SafeArea(
        child: Column(
          children: [
            if (widget.controller.offline) const OfflineBanner(),
            if (widget.controller.problem case final problem?)
              ProblemBanner(problem: problem),
            Expanded(
              child: Center(
                child: SingleChildScrollView(
                  padding: const EdgeInsets.all(24),
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 480),
                    child: Card(
                      elevation: 3,
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(20),
                        side: const BorderSide(color: Color(0xFFE2E8F0)),
                      ),
                      child: Padding(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 20,
                          vertical: 24,
                        ),
                        child: Form(
                          key: _formKey,
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              Center(
                                child: Container(
                                  width: 64,
                                  height: 64,
                                  decoration: BoxDecoration(
                                    color: const Color(0xFF0B4F6C).withValues(alpha: 0.08),
                                    shape: BoxShape.circle,
                                    border: Border.all(
                                      color: const Color(0xFF0B4F6C).withValues(alpha: 0.2),
                                      width: 2,
                                    ),
                                  ),
                                  child: const Icon(
                                    Icons.precision_manufacturing_rounded,
                                    size: 36,
                                    color: Color(0xFF0B4F6C),
                                  ),
                                ),
                              ),
                              const SizedBox(height: 16),
                              const Text(
                                'Operatör Terminali',
                                textAlign: TextAlign.center,
                                style: TextStyle(
                                  fontSize: 20,
                                  fontWeight: FontWeight.w900,
                                  color: Color(0xFF0F172A),
                                  letterSpacing: -0.5,
                                ),
                              ),
                              const SizedBox(height: 6),
                              Center(
                                child: Container(
                                  padding: const EdgeInsets.symmetric(
                                    horizontal: 12,
                                    vertical: 5,
                                  ),
                                  decoration: BoxDecoration(
                                    color: const Color(0xFFF1F5F9),
                                    borderRadius: BorderRadius.circular(20),
                                    border: Border.all(
                                      color: const Color(0xFFCBD5E1),
                                    ),
                                  ),
                                  child: Row(
                                    mainAxisSize: MainAxisSize.min,
                                    children: [
                                      Container(
                                        width: 8,
                                        height: 8,
                                        decoration: const BoxDecoration(
                                          color: Color(0xFF16835D),
                                          shape: BoxShape.circle,
                                        ),
                                      ),
                                      const SizedBox(width: 6),
                                      Flexible(
                                        child: Text(
                                          'Terminal: ${widget.controller.deviceCode}',
                                          style: const TextStyle(
                                            fontWeight: FontWeight.w700,
                                            fontSize: 12,
                                            color: Color(0xFF334155),
                                          ),
                                          overflow: TextOverflow.ellipsis,
                                        ),
                                      ),
                                    ],
                                  ),
                                ),
                              ),
                              const SizedBox(height: 28),
                              TextFormField(
                                key: const Key('employee-number-field'),
                                controller: _employeeNumber,
                                textInputAction: TextInputAction.done,
                                autocorrect: false,
                                style: const TextStyle(
                                  fontSize: 18,
                                  fontWeight: FontWeight.w700,
                                  letterSpacing: 1.0,
                                ),
                                decoration: InputDecoration(
                                  labelText: 'Sicil Numarası',
                                  labelStyle: const TextStyle(
                                    fontSize: 15,
                                    fontWeight: FontWeight.w600,
                                    color: Color(0xFF475569),
                                  ),
                                  hintText: 'Örn: 1042',
                                  prefixIcon: const Icon(
                                    Icons.badge_outlined,
                                    size: 24,
                                    color: Color(0xFF0B4F6C),
                                  ),
                                  filled: true,
                                  fillColor: const Color(0xFFF8FAFC),
                                  contentPadding: const EdgeInsets.symmetric(
                                    horizontal: 18,
                                    vertical: 18,
                                  ),
                                  border: OutlineInputBorder(
                                    borderRadius: BorderRadius.circular(12),
                                    borderSide: const BorderSide(
                                      color: Color(0xFFCBD5E1),
                                    ),
                                  ),
                                  enabledBorder: OutlineInputBorder(
                                    borderRadius: BorderRadius.circular(12),
                                    borderSide: const BorderSide(
                                      color: Color(0xFFCBD5E1),
                                    ),
                                  ),
                                  focusedBorder: OutlineInputBorder(
                                    borderRadius: BorderRadius.circular(12),
                                    borderSide: const BorderSide(
                                      color: Color(0xFF0B4F6C),
                                      width: 2,
                                    ),
                                  ),
                                ),
                                validator: (value) =>
                                    (value?.trim().isEmpty ?? true)
                                    ? 'Sicil numarası zorunludur.'
                                    : null,
                                onFieldSubmitted: (_) => _login(),
                              ),
                              const SizedBox(height: 24),
                              SizedBox(
                                height: 58,
                                child: ElevatedButton.icon(
                                  key: const Key('login-button'),
                                  style: ElevatedButton.styleFrom(
                                    backgroundColor: const Color(0xFF0B4F6C),
                                    foregroundColor: Colors.white,
                                    elevation: 2,
                                    shape: RoundedRectangleBorder(
                                      borderRadius: BorderRadius.circular(12),
                                    ),
                                    textStyle: const TextStyle(
                                      fontSize: 18,
                                      fontWeight: FontWeight.w800,
                                      letterSpacing: 0.5,
                                    ),
                                  ),
                                  onPressed: widget.controller.busy
                                      ? null
                                      : _login,
                                  icon: widget.controller.busy
                                      ? const SizedBox.square(
                                          dimension: 22,
                                          child: CircularProgressIndicator(
                                            strokeWidth: 2.8,
                                            color: Colors.white,
                                          ),
                                        )
                                      : const Icon(Icons.login_rounded, size: 22),
                                  label: const Text('Giriş Yap'),
                                ),
                              ),
                              const SizedBox(height: 16),
                              const Text(
                                'Yetkili üretim operatörü veya depo personeli siciliniz ile işlem yapabilirsiniz.',
                                textAlign: TextAlign.center,
                                style: TextStyle(
                                  fontSize: 12,
                                  color: Color(0xFF64748B),
                                  fontWeight: FontWeight.w500,
                                ),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    ),
  );

  Future<void> _confirmDeviceChange() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Cihazı değiştir / yeniden yapılandır'),
        content: const Text(
          'Bu terminal kimliği değiştirilecek. Oturum ve ekrandaki operasyon durumu temizlenecek; yeni cihazın Admin Web üzerinde önceden tanımlı olması gerekir.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Vazgeç'),
          ),
          ElevatedButton(
            key: const Key('confirm-device-code-change'),
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Yeniden Yapılandır'),
          ),
        ],
      ),
    );
    if (confirmed == true) await widget.controller.beginDeviceCodeChange();
  }
}
