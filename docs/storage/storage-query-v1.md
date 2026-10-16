# Storage read query v1 contract

## Scope and ownership

The current service exposes read-only calendar-period and inclusive local-date-range queries for keyboard aggregates and mouse-button counts. Mouse trajectory query and playback APIs were removed; current runtime code does not save or read mouse positions, routes, wheel movement, or event order.

## Explicit period semantics

`StoragePeriodResolver` accepts only an explicit, available Windows `TimeZoneInfo` identifier. Blank identifiers, IANA identifiers containing `/`, unknown period kinds, unavailable zones, and invalid or ambiguous local boundaries fail closed. Day, ISO Monday week, month, and year use adjacent local calendar boundaries and convert those boundaries to a non-empty UTC half-open range `[start,end)`. The Pacific spring and fall days therefore last 23 and 25 UTC hours; `China Standard Time` and `UTC` use the same algorithm without consulting the machine's later current zone.

Keyboard period queries include only durable `keyboard_daily` rows whose `time_zone_id` exactly equals the selected capture zone and whose local date is in `[localStart,localEnd)`. Results aggregate by persistent device and physical make/E0/E1 identity with checked non-negative counts. The result separately lists all capture-zone identifiers present for the selected keyboard scope. Daily privacy aggregates cannot be accurately reprojected from another capture zone, so the query never merges those rows and the UI must label the selected capture zone rather than claim a cross-zone total.

## Mouse button periods

Mouse queries require an optional persistent mouse GUID plus an explicit period and capture time zone. Results contain only per-device counts for left, right, middle, X1 and X2 button-down events. No position, route, wheel delta, event sequence or per-event timestamp is available through the public Storage surface.

Storage query results retain the Raw Input X1/X2 identifiers. The App dashboard maps raw X2 to the diagram's upper X1 and raw X1 to its lower X2; the overview uses the same display mapping. The Storage query and persisted aggregates keep their original identifiers.

## Evidence and limits

Ordinary tests cover four calendar periods and custom ranges, ISO Monday, Pacific 23/25-hour days, China/UTC, capture-zone isolation, keyboard device/key isolation, mouse-button device isolation, absence of trajectory artifacts and invalid inputs. This is software evidence only; it is not the required two-device terminal acceptance.
