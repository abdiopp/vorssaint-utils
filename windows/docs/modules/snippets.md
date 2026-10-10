# Text snippets (textSnippets) and the quick snippet menu

Spec: `docs/specs/06-clipboard-snippets-commandbar.md` §2.5–§2.6, §3.6–§3.7, §5.5, §6.12, §7.1; spec 05 §6.1.

## Where the code lives

| Layer | Files |
|---|---|
| Core | `src/Rivet.Core/Snippets/` — model, validation and settings (`TextSnippet`), trigger engine (`SnippetEngine`: buffer, word boundaries, immediate / after-delimiter, case rules), `SnippetExpansionService` (hook subscription, suspension, typing/pasting), variables (`SnippetVariables`), ICU/TR35 date patterns (`IcuDateFormat`), builder styles (`DateStyles`), time zones (`TimeZones`, generated IANA list), platform interfaces (`ISnippetPlatform`) |
| Windows | `src/Rivet.Platform.Windows/Snippets/WindowsSnippetPlatform.cs` — layout-aware key translation (`ToUnicodeEx` with flag 0x4 on the foreground thread's HKL), expansion sounds from `%windir%\Media` |
| Fake | `src/Rivet.Platform.Fake/Snippets/FakeSnippetPlatform.cs` |
| App | `src/Rivet.App/Features/Snippets/` — `SnippetsModule`, `SnippetLibraryWindow`, `SnippetEditorDialog` (with the date/time variable builder), `SnippetsSettingsPage` |
| Tests | `tests/Rivet.Core.Tests/Snippets/SnippetTests.cs` (trigger rules and matching, every variable token, TR35 formatting, time zones), `tests/Rivet.App.Tests/Snippets/` (snapshots) |

## Implemented

* **Expansion** through the shared `IInputHooks` keyboard hook (never an own hook). The hook callback only translates the key and feeds the engine; the expansion runs on the UI thread. A match swallows the last keystroke, deletes the typed trigger with backspaces and types the replacement with `SendInput` Unicode keystrokes; multi-line replacements are pasted with the transient paste (clipboard restored 0.5 s later, marks that keep Win+V history out).
* **Layouts and dead keys:** characters come from `ToUnicodeEx` with the "do not change keyboard state" flag (Windows 10 1607+), so pending dead keys in the real input are not disturbed; AltGr (Ctrl+Alt) is handled; a dead-key accent yields nothing (a trigger typed with a composed accent is seen without it — documented limit). Ctrl/Win combinations, navigation keys and clicks reset the buffer.
* **Privacy:** the buffer resets on every focus change (WinEvent foreground/focus hooks), on mouse clicks, while a password field has the focus (classic `ES_PASSWORD` and UI Automation `IsPassword`, checked on a worker thread with a time limit) and while the snippet menu or Command Bar is open. It is never persisted or logged. Windows does not deliver keystrokes typed into elevated windows to a standard-user hook (UIPI), so nothing is buffered or expanded there.
* **Variables:** `{{date}}`, `{{time}}`, `{{datetime}}`, `{{clipboard}}`, pattern tokens `{{date:…}}`/`{{time:…}}`/`{{datetime:…}}` with ICU/TR35 patterns and optional `-tz(<IANA id>)`. Patterns are interpreted by our own TR35 formatter, so tokens written on macOS (incl. U+202F) produce the same text. A malformed tag stays literal; the clipboard is inserted last so it is never re-expanded.
* **Editor** (dialog): name, trigger (validation: minimum length, duplicate triggers), mode immediate / after a delimiter, ignore case, folder, "show in menu", replacement, date/time builder (type, style, time-zone search, custom pattern, live preview, edit an existing token at the caret).
* **Quick snippet menu** (shortcut role `snippetLibrary`, Ctrl+Alt+Win+I): searchable, grouped by folder, Enter / click / Ctrl+1–9 types the snippet where the caret was (focus handed back first).
* **Expansion sound** (optional, any `.wav` of `%windir%\Media`).
* Controls switch "Text snippets" in the tray panel; Settings page in Mouse and keyboard; snippet rows in the Command Bar.

## Not implemented / deviations

* No cursor-placement variable: the macOS spec defines none, so none was added (the brief mentioned it in passing).
* IME composition (Chinese/Japanese/Korean input) is not observed: composed text does not reach the low-level hook as characters, so triggers typed through an IME do not expand. Triggers in Latin characters work under an IME's alphanumeric mode.
* Elevated apps (UIPI) and apps that read raw input never get expansions.
* Default menu shortcut is Ctrl+Alt+Win+I (spec 05 §6.1) instead of ⌃⌥⌘L.

## Risks

* Windows removes a low-level hook that is too slow; all work is outside the callback, but a stalled machine can still drop the shared hook (foundation concern).
* UI Automation calls into other processes can hang; the check is time-boxed, and when it times out the field is treated as not-a-password for that focus (classic password boxes are still caught by `ES_PASSWORD`).
* Apps that do not handle injected backspaces (some terminals, remote desktop clients) may leave the trigger in place.

## Manual test checklist (Windows)

1. Settings → Text snippets: add `;sig` → "Best regards,\nSam" (after a delimiter) and `;em` → `me@example.com` (immediate).
2. In Notepad type `;em`: replaced at once. Type `;sig` then Space: two lines pasted, the space kept; Ctrl+V afterwards still pastes what was on the clipboard before.
3. With a German or French layout type a trigger containing `ü`/`é` made with AltGr or a dead key: dead-key letters do not match (documented); AltGr letters do.
4. Focus a password field (Windows sign-in prompt in a browser, KeePass master password, a WinForms `UseSystemPasswordChar` box) and type a trigger: nothing expands.
5. Type half a trigger, click elsewhere, finish it: no expansion (buffer reset).
6. Run Notepad as administrator and type a trigger: no expansion, no partial deletion.
7. Add `{{date-tz(Asia/Tokyo):EEEE d MMMM HH:mm}}` with the builder; expand it and compare with a Tokyo clock. `{{clipboard}}` inserts the current clipboard text.
8. Press Ctrl+Alt+Win+I: the menu opens; search, press Enter: the snippet is typed in the previous app. Esc gives focus back without typing.
9. Turn the expansion sound on and pick another sound: it plays on each expansion.
10. Settings page renders in light and dark mode; the tray Controls switch turns expansion on/off.

## Requests for shared code

None.
