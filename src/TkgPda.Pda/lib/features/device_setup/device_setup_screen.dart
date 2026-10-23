import 'package:flutter/material.dart';

import '../../app/app_controller.dart';

class DeviceSetupScreen extends StatefulWidget {
  const DeviceSetupScreen({required this.controller, super.key});

  final AppController controller;

  @override
  State<DeviceSetupScreen> createState() => _DeviceSetupScreenState();
}

class _DeviceSetupScreenState extends State<DeviceSetupScreen> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _deviceCode;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    _deviceCode = TextEditingController(text: widget.controller.deviceCode);
  }

  @override
  void dispose() {
    _deviceCode.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    if (!_formKey.currentState!.validate() || _saving) {
      return;
    }
    setState(() => _saving = true);
    try {
      await widget.controller.saveDeviceCode(_deviceCode.text);
    } on FormatException catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(error.message)));
      }
    } finally {
      if (mounted) {
        setState(() => _saving = false);
      }
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('TKG Operator · Cihaz Kurulumu')),
    body: SafeArea(
      child: Center(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 520),
            child: Card(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Form(
                  key: _formKey,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      const Icon(Icons.phonelink_lock, size: 64),
                      const SizedBox(height: 18),
                      const Text(
                        'Önceden Tanımlı Operatör Cihazı',
                        textAlign: TextAlign.center,
                        style: TextStyle(
                          fontSize: 24,
                          fontWeight: FontWeight.w800,
                        ),
                      ),
                      const SizedBox(height: 10),
                      const Text(
                        'Admin Web üzerinden sağlanan PDA veya hat terminali cihaz kodunu girin. Uygulama yeni cihaz kaydı oluşturmaz.',
                        textAlign: TextAlign.center,
                      ),
                      const SizedBox(height: 24),
                      TextFormField(
                        key: const Key('device-code-field'),
                        controller: _deviceCode,
                        textCapitalization: TextCapitalization.characters,
                        autocorrect: false,
                        decoration: const InputDecoration(
                          labelText: 'Cihaz Kodu',
                          prefixIcon: Icon(Icons.tablet_android),
                        ),
                        validator: (value) {
                          final text = value?.trim() ?? '';
                          if (text.isEmpty) return 'Cihaz kodu zorunludur.';
                          if (text.length > 120) return 'Cihaz kodu çok uzun.';
                          return null;
                        },
                        onFieldSubmitted: (_) => _save(),
                      ),
                      const SizedBox(height: 20),
                      ElevatedButton.icon(
                        key: const Key('save-device-code'),
                        onPressed: _saving ? null : _save,
                        icon: _saving
                            ? const SizedBox.square(
                                dimension: 22,
                                child: CircularProgressIndicator(
                                  strokeWidth: 3,
                                ),
                              )
                            : const Icon(Icons.lock),
                        label: const Text('Cihaz Kodunu Kaydet'),
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
  );
}
