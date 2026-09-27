# Input device identity v1

## Purpose and boundary

This slice enriches each active Raw Input device generation with a private, structured identity observation. It does not assign a persistent device GUID. Storage remains the only owner of hashing, ambiguity policy, and persistent identity mapping.

The Raw Input callback continues to perform only packet normalization and bounded-queue admission. SetupAPI and Configuration Manager calls execute on the single Input worker when a nonzero `hDevice` is first observed. A repeated arrival or input for the same active handle does not repeat the query. Removal closes that generation; a subsequent arrival for the same handle is queried again and receives a new runtime identity.

## Private observation

`RawDeviceIdentityObservation` contains:

- device kind;
- Raw Input interface path;
- hardware ID list;
- serial number, currently always `null` because the documented device-instance properties used here do not expose an explicit serial-number property;
- container ID;
- transport assessment;
- evidence flags describing which properties were available.

The code never treats an instance-ID suffix, capability bit, interface path segment, or container ID as a serial number. Container ID alone is not a permanent identifier.

## Win32 sources

1. `GetRawInputDeviceInfoW(RIDI_DEVICENAME)` returns the interface path.
2. `SetupDiCreateDeviceInfoList`, `SetupDiOpenDeviceInterfaceW`, and `SetupDiGetDeviceInterfaceDetailW` resolve the path to a devnode.
3. `CM_Get_DevNode_PropertyW` reads the documented `DEVPKEY_Device_HardwareIds`, `DEVPKEY_Device_ContainerId`, `DEVPKEY_Device_BusTypeGuid`, and `DEVPKEY_Device_EnumeratorName` properties.

No registry API, service, driver, administrator permission, network access, or third-party dependency is introduced.

## Transport and failure policy

Classification uses explicit evidence:

- Bluetooth: Bluetooth bus GUID or BTH/BTHENUM/BTHLE enumerator/hardware ID;
- Remote Desktop: `ROOT\\RDP_KBD` or `ROOT\\RDP_MOU` evidence;
- known virtual: ROOT, VMBUS, or SWD enumerator/hardware ID after the more specific RDP case;
- physical: required hardware, bus, and enumerator properties were all read and none matched the preceding classes;
- indeterminate: any required classification property was unavailable.

Expected device-removal races, access denial, missing properties, malformed property buffers, and Win32 query failures degrade to a private observation whose transport is `Indeterminate` and whose public confidence is `RequiresConfirmation`. Input remains separated by its runtime ID. The provider does not place an interface path, hardware ID, serial, container ID, or other raw identity value in error text.

## Privacy

Raw identity is delivered only through the internal `IRawDeviceIdentityObserver`. The standalone default observer discards it; the production App supplies `AppStorageCoordinator`, which immediately maps the observation to Storage's hash-only device catalog. `CapturedInput` and its public `InputDeviceDescriptor` retain the existing API shape but arrivals intentionally carry `InterfacePath = null`; they never expose hardware IDs, serial numbers, or container IDs. UI and logging code must not receive the private observation.

The Input-to-Storage adapter maps `Indeterminate` to transient/requires-confirmation behavior and must not persist raw device identity values in SQLite, WAL, configuration, logs, CSV, PNG, or backups. Gamepad capture requires separate, explicit gamepad consent and is disabled by default; keyboard/mouse consent does not authorize it. Raw interface paths, hardware IDs, serial numbers, and container IDs remain private identity inputs and are reduced to hash-derived identity before persistence. Calibration stores the HID descriptor coordinates needed to recognize mapped controls, but not raw control values or event streams; user-facing diagnostics must not expose raw device identity values.

## Verification boundary

Ordinary tests cover property decoding, explicit transport classification, indeterminate fallback, same-generation query caching, handle reuse, private observer delivery, and public event redaction. They do not prove real hardware property availability. Final hardware evidence must cover two keyboards and two mice, replug behavior, same-model devices without serial numbers, Bluetooth/RDP/virtual classification, and 1000 Hz queue behavior without using mocks as terminal evidence.
