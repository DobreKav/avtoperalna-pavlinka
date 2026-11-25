"""The two Siemens programs share the bay logic: plc/FB_Peralna.scl (PLC = Modbus client) and
plc/FB_Peralna_Server.scl (PLC = Modbus server) differ only in the Modbus part. This test fails
when the shared part (from the "Bay (shared" marker to END_FUNCTION_BLOCK) is changed in one
file and not in the other, and checks the parts that both must have.

    python tests/plc_sources_test.py
"""
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MARK = "// ── Bay (shared"


def shared(name):
    with open(os.path.join(ROOT, "plc", name), encoding="utf-8") as f:
        text = f.read().replace("\r\n", "\n")
    start = text.index(MARK)
    return text[start:text.index("END_FUNCTION_BLOCK", start)]


def main():
    failures = 0
    a, b = shared("FB_Peralna.scl"), shared("FB_Peralna_Server.scl")
    if a == b:
        print("ok   bay logic is identical in FB_Peralna.scl and FB_Peralna_Server.scl")
    else:
        failures += 1
        print("FAIL bay logic differs between FB_Peralna.scl and FB_Peralna_Server.scl")
        for x, y in zip(a.splitlines(), b.splitlines()):
            if x != y:
                print("  FB_Peralna.scl:        " + x.strip())
                print("  FB_Peralna_Server.scl: " + y.strip())
                break
    for needle in ("#Enabled := #CommOk AND (#Regs[0] = 1) AND #MachineOk;",
                   "IF NOT #Enabled OR #BtnStop OR #HmiStop THEN",
                   "#Foam := #Enabled AND (#Program = 1);",
                   "#Water := #Enabled AND (#Program = 2);"):
        ok = needle in a
        failures += not ok
        print(("ok   " if ok else "FAIL ") + "bay logic has: " + needle)
    with open(os.path.join(ROOT, "plc", "Delta_DVP12SE.txt"), encoding="utf-8") as f:
        il = f.read()
    ok = "ANI M21\r\nOUT M20" in il or "ANI M21\nOUT M20" in il
    failures += not ok
    print(("ok   " if ok else "FAIL ") + "Delta program: M20 needs X3 (machine OK)")
    print("\n%s" % ("ALL OK" if not failures else "%d FAILED" % failures))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
