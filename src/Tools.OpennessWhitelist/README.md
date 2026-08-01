# Openness whitelist helper

`AutomationWorkbench.OpennessWhitelist.exe` manages the Siemens TIA Portal V17
Openness whitelist entry for the installed, final `Mcp.Engineering.exe`.

Commands:

```text
AutomationWorkbench.OpennessWhitelist.exe register --exe "<path>"
AutomationWorkbench.OpennessWhitelist.exe verify --exe "<path>"
AutomationWorkbench.OpennessWhitelist.exe remove --exe "<path>"
AutomationWorkbench.OpennessWhitelist.exe status --exe "<path>"
```

Stable exit codes:

```text
0  success
10 invalid arguments
11 executable missing
12 unsupported TIA version
13 elevation required
14 registry write failure
15 verification failure
16 hash calculation failure
```

Signing order is permanent:

```text
Build executable
→ sign executable when signing is introduced
→ install executable
→ calculate hash from installed signed file
→ register whitelist
```

The helper uses the same registry path, timestamp format, SHA-256 Base64 value,
and value names as `scripts/register-whitelist.ps1`.
