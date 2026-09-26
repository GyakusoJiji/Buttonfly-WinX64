# ButtonFly Specification

## Overview

ButtonFly is a Windows tray application that presents a configurable launcher menu. It supports Classic and Modern visual themes, nested menus, executable and folder launch actions, commands, and optional button icons.

## Launcher visibility

The launcher can be shown or hidden from the ButtonFly tray icon or by a global keyboard shortcut. Desktop-background click gestures are not supported.

The default shortcut is `Ctrl+Alt+B`. It can be changed in **Menu & Settings** under **Global show/hide shortcut**. A saved shortcut takes effect immediately.

### Shortcut format

Use one or more modifier names followed by one non-modifier key, separated by ASCII plus signs (`+`):

```text
Ctrl+Alt+B
Ctrl+Shift+B
Alt+F8
Ctrl+Win+M
Ctrl+Alt+NumPad1
```

At least one modifier is required. A key by itself, such as `B`, is not valid.

### Supported modifier names

- `Ctrl` or `Control`
- `Alt`
- `Shift`
- `Win`

Modifiers may be combined. For example, `Ctrl+Shift+M` is valid.

### Supported final keys

- Letters: `A` through `Z`
- Function keys: `F1` through `F24`
- Number-row keys: `D0` through `D9`
- Numeric keypad keys: `NumPad0` through `NumPad9`
- Navigation keys: `Left`, `Right`, `Up`, `Down`, `Home`, `End`, `PageUp`, `PageDown`, `Insert`, `Delete`
- Other keys: `Space`, `Tab`, `Enter`, `Escape`

The final key cannot be a modifier by itself. Windows-reserved shortcuts and shortcuts already registered by another application cannot be used; choose another combination if ButtonFly reports that it could not register the shortcut.

## Menu buttons

Each button can display one of the following icons:

- No icon
- The icon extracted from its launch target
- A selected `.ico` file

Folder menu items can also have icons. Modern theme buttons display an icon beside the title; Classic theme buttons display it above the title.
