import 'package:flutter/material.dart';

import '../../app/design_tokens.dart';

/// Standard, clear, calm empty list state for TKG PDA screens.
///
/// Designed to replace blank empty screens with helpful, descriptive feedback
/// indicating why the list is currently empty and what will cause items to appear.
class PdaEmptyState extends StatelessWidget {
  const PdaEmptyState({
    required this.icon,
    required this.title,
    this.subtitle,
    this.action,
    this.textKey,
    super.key,
  });

  final IconData icon;
  final String title;
  final String? subtitle;
  final Widget? action;
  final Key? textKey;

  @override
  Widget build(BuildContext context) => SingleChildScrollView(
    child: Padding(
      padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 32),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Container(
            width: 72,
            height: 72,
            decoration: BoxDecoration(
              color: const Color(0xFFF1F5F9),
              shape: BoxShape.circle,
              border: Border.all(color: const Color(0xFFCBD5E1), width: 1.5),
            ),
            child: Icon(icon, size: 36, color: const Color(0xFF64748B)),
          ),
          const SizedBox(height: 16),
          Text(
            title,
            key: textKey,
            textAlign: TextAlign.center,
            style: TextStyle(
              fontSize: PdaScale.title(context) * 0.9,
              fontWeight: FontWeight.w700,
              color: const Color(0xFF1E293B),
            ),
          ),
          if (subtitle case final sub?) ...[
            const SizedBox(height: 8),
            ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Text(
                sub,
                textAlign: TextAlign.center,
                style: TextStyle(
                  fontSize: PdaScale.body(context),
                  color: const Color(0xFF64748B),
                  height: 1.4,
                ),
              ),
            ),
          ],
          if (action case final act?) ...[const SizedBox(height: 20), act],
        ],
      ),
    ),
  );
}
