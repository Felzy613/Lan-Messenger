# StatusTicks

The delivery state of an outgoing message, drawn as WhatsApp ticks in the meta row.

## States
- **Sent** (also Sending, Queued, unset): one tick in the meta colour. Every in-flight state collapses to this so the icon never flickers on a fast LAN.
- **Delivered:** two ticks in the meta colour.
- **Read:** two ticks in `tick-read`.
- **Failed:** an exclamation circle in `danger-ink`.

## Rules
- Status only moves up (Sent, then Delivered, then Read). A late Sent callback must never redraw a Delivered or Read tick.
- The ticks are 10pt glyphs. The double tick's second check is offset 4px, in a 14×10 frame.
- Read is told apart by shape (two ticks) as well as colour.

## Build
`BubbleStatusView` on macOS and the status glyphs in `MessageBubbleControl` on Windows. Both must switch `tick-read` per theme.
