# U-A build notes: interface redesign

**Built and tested here**: BimGo.Core and its tests (all pass except the Linux-only
`SunStudyTests.Studies_WriteListReadDelete`, as before). BimGo.App compile-checked against the Windows Desktop
reference assemblies. **Not run** (no Windows / GPU here).

## Files
- Core `Scene/LaunchSettings.cs`: `UiTheme`, `UiAccent`, `UiScale`, `UiTextSize`, `UiFont`, `UiNumbersFont`,
  `HudLayout`, `HudFade` (cleaned in `Sanitise`); `UiThemes`, `HudLayouts`. Test:
  `LaunchSettingsTests.Interface_DefaultsKeepTheOldLookAndBadValuesAreCleaned`.
- App `Game/UiTheme.cs`: themed colours as properties set by `Apply`; `Ink`, `Shade`, `Accent`; `ACCENT_SOFT`,
  `ON_ACCENT`; `RADIUS`, `RADIUS_SMALL`. About 150 hard-coded panel colours across the panels now use them.
- App `Rendering/UiFonts.cs` (new), `UiFont.cs` (roles from the chosen fonts, the corner disc and ring),
  `UiBatch.cs` (`Rebuild`, `Generation`, `Opacity`, `Radius`, `RoundRect`, `RoundOutline`); `Resources/Fonts` (11 TTF
  files and 4 OFL texts, about 4.3 MB), the csproj copy rule, the publish check, THIRD-PARTY-NOTICES.
- App `Game/GameSession.Interface.cs` (new): settings, the INTERFACE tab, the pause menu sidebar and icons, HUD
  layouts and fade, `UiLook`. Hooks: `GameSession.cs` (read / write settings, the scale, the fade, the search reset),
  `Menu.cs` (the sidebar in place of the stacks, the INTERFACE tab, rounded `MenuButton` / `Segmented` / `Checkbox` /
  `Slider`), `PanelShell.cs` (rounded `ShellButton`, scrollbars, Esc clears the search), `Comments.cs`,
  `Overlays.cs`, `Render.cs` (HUD layouts, the help sheet re-measured when the fonts change), `Video.cs`,
  `Shell/AppShell.cs` (the home screen).

## Decisions worth knowing
- **The dark theme looks as before** (same colours, the default fonts unchanged): only the corners are new, and the
  Standard HUD drops two status rows (ground, colour mode) that Full keeps.
- **Anti-aliased corners without a new shader**: a disc and a ring drawn once into the font atlas; each corner is a
  textured quad of one quarter, clipped like any quad.
- **Fonts are loaded privately**, never installed into Windows, and fall back to Segoe UI / Courier New face by face.
- **Sizes and fonts apply when let go / picked**: rebuilding the atlas takes a moment, so it isn't done per frame.
- **Tool colours don't follow the theme**: scan cyan, measure amber, comment violet etc. mean the same thing
  everywhere; only their label shades darken on the light theme.
- **No hard-coded white over panels**: hover tints, tracks and hairlines use `UiTheme.Ink`, which is dark on the
  light theme. Images and marks drawn over the model keep their own colours.
