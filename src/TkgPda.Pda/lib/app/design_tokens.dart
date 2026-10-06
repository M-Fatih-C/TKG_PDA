import 'package:flutter/material.dart';

/// Design tokens for the Operator client.
///
/// The client runs on two very different surfaces — a 1920x1080 Windows
/// production-line touch terminal read while standing, and a ~1024x600 Android
/// industrial PDA held in one hand — from one codebase. Before these tokens
/// existed the theme declared a reasonable scale (56px buttons, 17px labels)
/// but the busiest surface, the WP-33 container panel, overrode it down to 12px
/// labels with 4x2 padding. Sizes now come from one place so a screen cannot
/// silently undercut the scale again.
///
/// Nothing here changes business behaviour; these are presentation values only.
class PdaScale {
  const PdaScale._();

  /// Below this width the terminal layout collapses to a single column.
  static const double twoColumnBreakpoint = 1280;

  /// Below this width the compact (handheld PDA) type scale applies.
  static const double compactBreakpoint = 1024;

  /// A 1024×600 industrial terminal is short even though its width is not
  /// below [compactBreakpoint]. Height and the shortest side decide that.
  static bool isShortTerminal(BuildContext context) {
    final size = MediaQuery.sizeOf(context);
    return size.height <= 640 || size.shortestSide <= 600;
  }

  /// True on the line terminal, where the operator reads from a distance and
  /// the screen can carry two columns at once.
  static bool isWide(BuildContext context) =>
      MediaQuery.sizeOf(context).width >= twoColumnBreakpoint &&
      !isShortTerminal(context);

  /// Production workspace columns. A 1024×600 industrial terminal is short,
  /// so [isWide] is false there, but it still has room for Mamul Kasa and
  /// active transports beside the alt-part list. Phones stay one column.
  static bool usesSideColumn(BuildContext context) =>
      MediaQuery.sizeOf(context).width >= 960;

  /// True on a handheld or short industrial terminal.
  static bool isCompact(BuildContext context) {
    final size = MediaQuery.sizeOf(context);
    return size.width < compactBreakpoint || isShortTerminal(context);
  }

  // --- Type scale -------------------------------------------------------
  // Terminal values first, handheld values after the slash in the comment.
  // 16px is the floor: below it the text is not readable at arm's length on a
  // line terminal, which is what produced the 9px "disabled reason" nobody
  // could read.

  static double title(BuildContext context) => isCompact(context) ? 17 : 25;
  static double primaryAction(BuildContext context) =>
      isCompact(context) ? 18 : 25;
  static double secondaryAction(BuildContext context) =>
      isCompact(context) ? 16 : 21;
  static double body(BuildContext context) => isCompact(context) ? 15 : 18;
  static double caption(BuildContext context) => isCompact(context) ? 13 : 16;

  // --- Touch targets ----------------------------------------------------
  // Material's 48px minimum assumes a bare fingertip on a phone. These are
  // pressed with work gloves, often without looking directly at the screen.
  // The primary action takes whatever height its card has left, so only the
  // secondary floor needs stating.

  static double secondaryActionHeight(BuildContext context) =>
      isCompact(context) ? 52 : 72;

  /// Hard ceiling for stacked status banners. A 600px terminal must keep the
  /// production workspace visible, so this stays well under a third of the
  /// screen. Extra messages scroll inside the cap.
  static double statusRegionMaxHeight(BuildContext context) {
    final height = MediaQuery.sizeOf(context).height;
    final fraction = height * (isShortTerminal(context) ? 0.16 : 0.22);
    return fraction.clamp(72, 160);
  }

  /// Height of the docked container panel. The cards flex into whatever this
  /// gives them, so the panel can grow when it moves into a two-column layout
  /// without the card having to be rewritten.
  static double containerPanelHeight(BuildContext context) =>
      isCompact(context) ? 208 : 292;

  /// A card taller than this stops being a card and becomes a wall: the
  /// default stadium-shaped button stretched to a full column renders as a
  /// giant capsule. In a side column the cards keep this height and the
  /// leftover space goes to the bulk action at the bottom.
  static double containerCardMaxHeight(BuildContext context) =>
      isCompact(context) ? 260 : 520;

  /// Corner radius for the action buttons. Stated explicitly because the
  /// Material 3 default is a stadium, which is only sane on a short button.
  static const double actionRadius = 14;

  /// Above this the card can afford to stack its two actions vertically and
  /// give the available one the larger share; below it they sit side by side.
  static double stackedActionsMinHeight(BuildContext context) =>
      isCompact(context) ? 190 : 250;

  // --- Spacing ----------------------------------------------------------
  static double gutter(BuildContext context) => isCompact(context) ? 10 : 16;
  static double cardPadding(BuildContext context) =>
      isCompact(context) ? 13 : 20;
}

/// Colour for a transport presentation state.
///
/// These are the states the backend projection exposes to the operator, not
/// [TransportRequestStatus]. Severity has to be legible at a glance from across
/// the line, so every state maps to one of five meanings — previously they all
/// rendered in the same neutral grey chip and "Hazır" looked like "Hata".
class PdaStatusColors {
  const PdaStatusColors._();

  static const Color done = Color(0xFF16835D);
  static const Color running = Color(0xFF2F6FED);
  static const Color waiting = Color(0xFFC26D16);
  static const Color failed = Color(0xFFC94040);
  static const Color unknown = Color(0xFF8791A3);

  static Color of(String state) => switch (state) {
    'READY' || 'COMPLETED' => done,
    'REQUESTED' || 'QUEUED' || 'ROBOT_ASSIGNED' || 'IN_PROGRESS' => running,
    'WAITING_PREPARATION' => waiting,
    'ERROR' => failed,
    _ => unknown,
  };

  /// Tinted background for a chip carrying [of]'s colour as its foreground.
  static Color surfaceOf(String state) => of(state).withValues(alpha: 0.12);
}
