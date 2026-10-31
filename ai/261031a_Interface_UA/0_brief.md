# U-A brief: interface redesign

Asked 2026-10-31 (the same user as V-A, P-A and M-A): another PR redesigning the whole UI: a font system people can
choose, UI scale, and anything else that makes it cleaner and easier to use.

## Decisions (asked and answered)
- **Restyle + reorganise**: a new look everywhere (rounded cards, calmer palette, consistent controls, icons), and the
  pause menu becomes a sidebar with sections (Model, Review, Media, Settings) and a search box. Panels keep their
  features.
- **Fonts: bundled + installed**: ship a few open-licence fonts (Inter, Roboto, Source Sans 3, JetBrains Mono) and
  allow any installed Windows font.
- **Themes: dark, light, high contrast**, plus a choice of accent colour.
- **Extras: HUD layouts** (Minimal / Standard / Full) and panels that fade when not in use. (Command search,
  tooltips everywhere and a first-run tour were offered and not chosen.)
- Stacked on the materials PR (`ifc-materials`), merged last.
