# RemoteAuditRow

A remote-desktop event recorded in the thread, centred on flat glass like WhatsApp's system messages.

## Events
- Session started: eye icon, "Priya viewed your screen", then the duration once it ends.
- Control granted: hand icon.
- Control revoked: crossed-out hand.
- Session ended: stop icon.

## Rules
- Write the sentence from the reader's chair. The record carries `viewing`, and every sentence has a host form and a viewer form ("You viewed Priya's screen").
- The summary is `caption` with the actor in semibold `ink`. The duration is in `ink-secondary`, and the time below it is `micro` in `ink-secondary`, not dimmed further.
- The chip is `glass-regular` with `lm-glass--flat`: no float shadow, because it is part of the thread, not chrome over it.
