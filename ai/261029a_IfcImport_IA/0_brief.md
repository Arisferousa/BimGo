# I-A brief: IFC import (Revit LT)

Asked 2026-10-29 (a Revit LT user): "I use Revit Lite and would like to try this feature … develop a feature to either
import from a Revit file or from IFC exported from Revit. Choose the most plausible option."

## The choice: IFC, not .rvt
- **Revit LT has no API and runs no add-ins**, so BimGo for Revit (Go, Export .bimgo) can't run there.
- **A `.rvt` can only be read by Revit**: the format is proprietary and undocumented (OLE compound file with compressed,
  version-specific element streams); the only supported readers are Revit itself, Autodesk Platform Services (cloud, a
  paid account, uploads the model) and the ODA BimRv SDK (commercial membership). None fits a free, offline, MIT-0 app
  with no packages without Gavin's yes.
- **Revit LT exports IFC** (File → Export → IFC, the same open-source exporter as full Revit: IFC2x3 Coordination View
  2.0 by default, IFC4 Reference View, …). IFC is an open ISO standard (16739), keeps GUIDs, names, types, levels,
  rooms and property sets, and is already half-supported by BimGo (IFC GUIDs for BCF). The roadmap already listed "IFC
  import" under *later*.

## Shape of it
- Core only, no package (DEVELOPING rule 5: own what differentiates BimGo; a parser and a small CSG are plumbing we can
  own in a few thousand lines rather than asking for xBIM / IfcOpenShell, which are large, LGPL / native respectively).
- Opening an `.ifc` imports it into an ordinary in-memory `BimGoDocument` (no path), so every walkthrough tool, save,
  journal, comments, BCF, clashes and kiosk mode work unchanged. Nothing is written until SAVE.
- Mapped onto what the Revit extractor produces: catalog categories, "Family: Type", level names, hosts, rooms with
  parameters, grids, entourage slots, site location, IFC GUIDs.
