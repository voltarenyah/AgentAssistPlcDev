# Extended Change Detection in TIA Portal Projects Beyond Software Checksum and Hardware AML

This document lists modifications that **cannot** be reliably detected using only the **PLC software checksum** and the **hardware configuration AML** file. For each item, the following details are provided:

- **Data not tracked**: The specific content that changes.
- **Accessibility**: How the data can be read or exported using TIA Openness API, project export functions, or direct project file parsing.
- **Recommended difference/verification method**: How to detect changes for that data.

---

## 1. Program and Code Related (Non-Compiled Content)

| Item | Data not tracked | Accessibility | Recommended difference/verification method |
|------|------------------|---------------|---------------------------------------------|
| Block comment | FB/FC/OB/DB block comments | Openness: `IBlock.Comment` or `IBlock.GetAttribute("Comment")` | Read all block comments, generate text hash (e.g., SHA-256) and compare |
| Network comment | Network titles and comments | Openness: export block XML or read `INetwork.Comment` | Export block XML, extract comment nodes, generate hash or XML diff |
| Segment title (Network title) | Title of each network | Same as above | Export XML and extract titles, compute hash |
| Tag comment | PLC tag table, DB variable, and interface variable comments | Openness: `IPlcTag.Comment`, `IVariable.Comment` | Traverse all variables, collect comments, generate hash |
| Symbolic names / variable naming | Names of variables, blocks, data types, OBs (not referenced by code logic) | Openness: traverse project tree to get all object names | Generate a list including object paths and names, compute hash |
| PLC data type (UDT) comment | Comments for each element in UDT | Openness: `IDataType` interface | Same as above |
| Project text library / multilingual translations | All translation strings | TIA Portal menu: Tools → Export texts; or Openness possibly via `IProjectText` | Export text file (.xlsx or .txt), compute file hash |
| Folder structure / naming | Folders, groups, hierarchy in project tree | Openness: traverse `IProjectTree` to get all nodes and paths | Generate path tree structure text, compute hash |
| User-defined attributes | Arbitrary custom attributes on objects | Openness: `GetAttribute()` or specific interfaces | Collect all custom attribute key-value pairs, serialize and hash |

---

## 2. HMI / WinCC Configuration

| Item | Data not tracked | Accessibility | Recommended difference/verification method |
|------|------------------|---------------|---------------------------------------------|
| HMI screen layout and graphical objects | Graphical elements, positions, sizes, colors, animations, events | Openness: `IHmiScreen` may be limited; recommended to use HMI export function (screens exported as XML) | Export all screens as XML, compute hash per file or entire package |
| HMI tag connections | HMI tags or PLC tags connected to screen objects | Parse exported screen XML, or read screen object properties via Openness | Extract connection information, generate list hash |
| HMI alarms (discrete/analog) | Alarm text, trigger tag, category, priority | Openness: `IAlarm` interface; or export alarm texts | Export alarm configuration or traverse alarm objects, generate structured data hash |
| HMI recipes | Recipe data records, tag mappings | Openness: `IRecipe` interface may support reading; or via HMI export | Export recipes as CSV/XML, hash compare |
| HMI user administration | Users, user groups, permissions, passwords (encrypted) | Openness: `IUserAdministration` or `IHmiTarget` related interfaces | Read user list and permissions, serialize and hash (passwords irreversible, only compare existence) |
| HMI scripts (VBS/JS) | Script code content | Openness: `IScript` object or export screen/global script files | Export all script files, compute hash |
| HMI data logs / trends | Logged variables, storage settings, trend view configuration | May require HMI export or specific Openness interfaces | Export relevant configuration, hash |
| HMI text lists | Text list entries, languages | Export text lists (Tools → Export texts) | Export file hash |
| HMI user-defined controls / libraries | Custom control files or library objects | Project additional files or HMI library directory | Collect relevant files, hash |

---

## 3. Communication Connections and Network Configuration

| Item | Data not tracked | Accessibility | Recommended difference/verification method |
|------|------------------|---------------|---------------------------------------------|
| S7 connection parameters | Connection partner, local/remote TSAP, rack/slot, connection type | Openness: `IConnection` interface, read properties such as `ConnectionType`, `Partner`, `LocalId` | Traverse all connections, generate parameter list hash |
| TCP/UDP/ISO-on-TCP connections | Port, IP address, connection mode | Same as above via `IConnection` properties | Same as above |
| OPC UA server configuration | Enable status, port, security policies, certificates, nodes | Openness: `IOpcUaServer` interface (version dependent) | Read configuration properties, generate hash |
| OPC UA client configuration | Connection endpoints, subscribed variables | Possibly via `IOpcUaClient` interface | Same as above |
| Modbus TCP configuration | Slave address, function code mapping, registers | Usually via CP or CM module parameters, Openness can read module parameters | Read module parameters, serialize hash |
| PROFINET IO device name/IP | Device name, IP address, subnet mask, gateway | Hardware AML may contain, but topology view device name/IP may be missing; can be read from device properties | Traverse devices, read PN interface properties, generate list hash |
| Network topology (port interconnections) | Which port connects to which device | Hardware AML may not include topology; Openness: `ITopology` interface can read topology connections | Export topology information, hash |
| MRP ring configuration | MRP enable, role, ring ports | Device interface properties or topology interface | Generate configuration hash |
| Isochronous mode configuration | Isochronous mode, sync domain, send clock | Device properties | Same as above |
| Web server settings | Enable, port, user permissions, custom web pages | CPU properties (possibly via Openness `IServer` interface) | Read relevant properties, hash |
| Custom web page files | Uploaded HTML/images etc. | Project directory `UserFiles` or via Openness to get additional files | Collect files, compute hash |
| Firewall / VPN / NAT | Security rules, VPN configuration | Usually in CP or CPU properties, Openness support limited | Parse relevant XML parts in project file |

---

## 4. Technology Objects and Motion Control

| Item | Data not tracked | Accessibility | Recommended difference/verification method |
|------|------------------|---------------|---------------------------------------------|
| Axis technology object parameters | Velocity, acceleration, jerk, position limits, homing parameters | Openness: `ITechnologyObject` interface, read axis properties | Traverse axis objects, read all parameters, generate structured hash |
| Synchronous axes / gear ratio | Master axis, slave axis, gear ratio numerator/denominator | Same as above | Same as above |
| Cam curves / cam disks | Cam points, interpolation, scaling | Possibly via `ICam` or export cam data | Export cam table, hash |
| Kinematics | Axis groups, coordinate transformation, path interpolation | `IKinematics` interface | Read parameters, hash |
| PID technology object | Proportional, integral, derivative, output limits | `ITechnologyObject` (type PID) | Same as above |
| High-speed counters (HSC) | Counting mode, comparison values, interrupt settings | Technology objects in device configuration, Openness can read | Same as above |
| PWM / pulse outputs | Frequency, duty cycle, enable | Same as above | Same as above |
| DCC / S7-Technology charts | Chart logic, connections, parameters | DCC may not be accessible via standard Openness; export project or parse project files directly | Export DCC chart files, hash |

---

## 5. Safety Program / Failsafe

> **Update 2026-09-01 (issue #67, runtime-verified):** the V17 assembly contains an undocumented
> `Siemens.Engineering.Safety` namespace — `SafetySignatureProvider.Signatures.Find(BlockOfflineSignature)`
> gives the offline collective F-signature per PLC. It is wired into `get_plc_checksums`
> (`PlcChecksumInfo.FSignature`) and recorded in `revision.json`; safety-only edits now classify
> as `SafetyChanged`. Verified against a live F-CPU project (CPU 1515F-2 PN): the safety services
> are anchored on the PLC's **DeviceItem** (not the PlcSoftware), and `SafetySignatureProvider`
> is **license-gated** — null on machines without a STEP 7 Safety license, where F-block
> fingerprints remain the fallback signal. See
> `buildnote/bestpractice/openness-v17-api-surface.md` §11 and `scripts/Probe-SafetySignature.ps1`.

| Item | Data not tracked | Accessibility | Recommended difference/verification method |
|------|------------------|---------------|---------------------------------------------|
| F-CPU safety parameters | Safety mode, F monitoring time, safety address | Openness: safety-related interfaces (`ISafetyProgram` or device properties) | Read safety parameters, hash |
| F-I/O parameters | Sensor evaluation, channel parameters, passivation behavior | Same as above | Same as above |
| Safety program logic | F-FB/F-FC content | F blocks may be protected, but can be exported or signature read | Export F program blocks, compute hash or F collective signature |
| F collective signature | Signature of the entire safety program | Openness or project file can read | Directly compare signature string |

---

## 6. Project Protection and Access Management

| Item | Data not tracked | Accessibility | Recommended difference/verification method |
|------|------------------|---------------|---------------------------------------------|
| PLC access protection level | Read/write/full access password levels | Openness: `IProtection` interface or device properties | Read protection level and hashed password (irreversible), compare level |
| Block know-how protection | Which blocks have know-how protection enabled | Openness: `IBlock.IsKnowHowProtected` | Generate list of block protection status, hash |
| HMI user permissions | User groups, function permissions | See HMI user administration section | Same as above |
| Project password | Project opening password | May not be readable, only parse from project file header | Compare password hash (if extractable) |

---

## 7. Online / Debug Data (May Not Exist in Offline Project)

| Item | Data not tracked | Accessibility | Recommended difference/verification method |
|------|------------------|---------------|---------------------------------------------|
| Watch tables | Monitored variables, addresses, trigger conditions | Watch tables are typically not part of offline project, Openness cannot read directly; can use export/import files or online interfaces | If watch table export files exist in project, hash compare; otherwise cannot detect offline |
| Force tables | Forced variables and force values | Same as above, online state only | Cannot detect offline; requires online connection |
| Trace configurations | Trace variables, sampling period, trigger conditions | Trace configuration may be stored in CPU or separate files; no direct Openness interface | Export trace configuration, hash |
| Actual values of data blocks | Current values of DB variables | Offline project only has initial values; actual values require online read | Read online and compare, or export DB actual value snapshot |
| Diagnostics buffer content | Event logs | Online data, not part of project | Cannot detect offline |

---

## 8. External Files and Additional Resources

| Item | Data not tracked | Accessibility | Recommended difference/verification method |
|------|------------------|---------------|---------------------------------------------|
| Custom web page files | HTML, CSS, JS, images | Project directory `UserFiles\WebServer` or via Openness to get additional files | Collect all files in directory, compute overall hash |
| Recipe CSV/TXT files | File content | Project additional file directory | Hash files |
| User-defined libraries | Library version, library elements | Openness can access library objects, or compare library references in project | Export library content or read library version, hash |
| Imported/exported SCL source files | Source file content | Project directory `Sources` or Openness read `ISource` | Hash source files |
| Documents / images / scripts | Additional files | Project directories `UserFiles`, `Documents`, etc. | Hash file collection |
| Custom controls / DLLs | Binary files | Project installation directory or additional files | Hash files |

---

## 9. Hardware AML Blind Spots (Require Supplement from Device Properties)

| Item | Data not tracked | Accessibility | Recommended difference/verification method |
|------|------------------|---------------|---------------------------------------------|
| Topology view port interconnections | Physical connections between devices | Openness: `ITopology` interface | Traverse topology connections, generate connection pair list, hash |
| Port parameters (negotiation, energy saving) | Port speed, duplex, energy-efficient Ethernet | Openness: device interface properties `IImportedInterface` or `IPort` | Read port parameters, hash |
| Isochronous mode configuration | Isochronous mode enable, sync domain | Device properties | Same as above |
| MRP ring configuration | MRP role, ring ports | Device interface properties or topology interface | Same as above |
| PROFINET device name/IP | Device name, IP address (sometimes missing in AML) | Device properties `IProfinetInterface` | Same as above |
| Firmware version compatibility | Allowed firmware version range | Device properties | Same as above |
| Module advanced parameters (diagnostics, interrupt enable) | Specific parameters of each module | Device parameter interface | Same as above |
| Hardware interrupts / diagnostic settings | Which events trigger OBs | Module parameters or CPU properties | Same as above |

---

## Summary and Recommendations

- **Objects directly readable via Openness**: comments, variables, connections, technology objects, protection settings, topology, etc. Use API traversal and generate structured hashes.
- **Content requiring export**: HMI screens, alarms, recipes, text libraries, DCC charts. Use TIA Portal export functions or Openness export methods to obtain files, then compute hashes.
- **Direct project file parsing**: For properties not exposed by Openness (e.g., some safety parameters, firewall rules), unzip the `.ap` file (TIA project is essentially a ZIP) and parse relevant XML fragments.
- **Online data**: Watch tables, force tables, DB actual values are not part of the offline project. They cannot be compared offline; require online connection or separate backup files.

By building an **extended project fingerprint** using the above approaches, change detection coverage can be significantly improved.