"""Delta DVP12SE test: the PLC program, the Modbus map and the whole chain
card -> PeralnaAgent.exe -> Modbus TCP -> DVP12SE (emulated) -> billing, with a throwaway database.

    python tests/delta_dvp12se_test.py

Needs C:\\xampp\\php\\php.exe and the csc.exe that ships with Windows (the agent is compiled into a
temporary folder). Takes about a minute and a half: the watchdog and the billing run in real time.
"""
import os
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from delta_dvp12se_sim import Dvp12se, ModbusServer, D_BASE, ROOT  # noqa: E402

PHP = os.environ.get("PHP", r"C:\xampp\php\php.exe")
CSC = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), r"Microsoft.NET\Framework64\v4.0.30319\csc.exe")
CARD, HOLDER = "DE17A001", "Тест Делта"

failures = 0
try:
    sys.stdout.reconfigure(encoding="utf-8")   # Cyrillic labels on a Windows console
except AttributeError:
    pass


def check(label, ok, detail=""):
    global failures
    if ok:
        print("ok   " + label)
    else:
        failures += 1
        print("FAIL " + label + (": " + str(detail) if detail else ""))


def wait(cond, timeout, step=0.05):
    end = time.time() + timeout
    while time.time() < end:
        if cond():
            return True
        time.sleep(step)
    return cond()


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


# ── 1. The ladder program on its own ─────────────────────────────

def test_program():
    print("-- PLC program (plc/Delta_DVP12SE.txt)")
    plc = Dvp12se()
    plc.start()
    beat = {"on": True}

    def laptop():                       # what the agent does: heartbeat every second
        while beat["on"]:
            with plc.lock:
                plc.D[5] = (plc.D[5] + 1) & 0xFFFF
            time.sleep(1)

    time.sleep(0.2)
    check("after power on everything is off", plc.outputs() == (False, False, False, False), plc.outputs())
    plc.pulse("X0")
    check("ПЕНА without a card does nothing", plc.outputs()[0] is False and plc.d(10) == 0)

    with plc.lock:
        plc.D[0], plc.D[1] = 1, 125
    threading.Thread(target=laptop, daemon=True).start()
    time.sleep(0.3)
    check("X3 'machine OK' open: no ready lamp, D11 = 1", not plc.outputs()[2] and plc.d(11) == 1, (plc.outputs(), plc.d(11)))
    plc.pulse("X0")
    check("X3 open: ПЕНА does nothing", not plc.outputs()[0] and plc.d(10) == 0)
    plc.press("X3")                     # E-stop, motor protection and water level all OK
    check("card inside: Y2 ready lamp", wait(lambda: plc.outputs()[2], 1))
    check("X3 closed: D11 = 0", plc.d(11) == 0)
    check("mm:ss of 125 s is 2:05 (D310/D311)", wait(lambda: (plc.d(310), plc.d(311)) == (2, 5), 1),
          (plc.d(310), plc.d(311)))

    plc.pulse("X0")
    check("ПЕНА: Y0 on, D10 = 1", wait(lambda: plc.outputs()[:2] == (True, False) and plc.d(10) == 1, 1),
          (plc.outputs(), plc.d(10)))
    check("ПЕНА stays on after the button is released", plc.outputs()[0])
    plc.pulse("X1")
    check("ВОДА: Y1 on, Y0 off, D10 = 2", wait(lambda: plc.outputs()[:2] == (False, True) and plc.d(10) == 2, 1))
    with plc.lock:
        plc.X[0] = plc.X[1] = True
    check("ПЕНА wins when both buttons are held", wait(lambda: plc.d(10) == 1, 1))
    with plc.lock:
        plc.X[0] = plc.X[1] = False
    plc.pulse("X2")
    check("СТОП: both off, D10 = 0", wait(lambda: plc.outputs()[:2] == (False, False) and plc.d(10) == 0, 1))
    plc.press("X2")
    plc.pulse("X0")
    check("ПЕНА is ignored while СТОП is held", plc.d(10) == 0 and not plc.outputs()[0])
    plc.press("X2", False)

    with plc.lock:
        plc.M[100] = True
    check("touch panel M100 = ПЕНА", wait(lambda: plc.outputs()[0], 1))
    with plc.lock:
        plc.M[100] = False
        plc.M[102] = True
    check("touch panel M102 = СТОП", wait(lambda: not plc.outputs()[0], 1))
    with plc.lock:
        plc.M[102] = False

    plc.pulse("X0")
    with plc.lock:
        plc.D[0] = 0
    check("ENABLE 0 (card out): foam off and D10 = 0", wait(lambda: not plc.outputs()[0] and plc.d(10) == 0, 1))
    with plc.lock:
        plc.D[0] = 1
    plc.pulse("X1")
    check("water on again", wait(lambda: plc.outputs()[1], 1))
    plc.press("X3", False)              # E-stop pressed / motor protection tripped / no water
    check("X3 opens: water off at once, D10 = 0, D11 = 1",
          wait(lambda: not plc.outputs()[1] and plc.d(10) == 0 and plc.d(11) == 1, 1), (plc.outputs(), plc.d(10), plc.d(11)))
    check("X3 open: ready lamp off", not plc.outputs()[2])
    plc.pulse("X1")
    check("X3 open: ВОДА is ignored", not plc.outputs()[1])
    plc.press("X3")
    time.sleep(0.2)
    check("X3 closed again: nothing starts by itself, D11 = 0", not plc.outputs()[0] and not plc.outputs()[1] and plc.d(11) == 0)
    plc.pulse("X1")
    check("water on again after X3", wait(lambda: plc.outputs()[1], 1))

    beat["on"] = False
    t0 = time.time()
    check("no heartbeat: water off within 5-6.5 s", wait(lambda: not plc.outputs()[1], 7))
    took = time.time() - t0
    check("watchdog did not fire too early (%.1f s)" % took, took >= 4.0, took)
    check("Y3 'no link' lamp on, D10 = 0", plc.outputs()[3] and plc.d(10) == 0)
    plc.pulse("X0")
    check("ПЕНА refused while the link is dead", not plc.outputs()[0])
    beat["on"] = True
    threading.Thread(target=laptop, daemon=True).start()
    check("heartbeat back: Y3 off", wait(lambda: not plc.outputs()[3], 2.5))
    check("heartbeat back: nothing starts by itself", not plc.outputs()[0] and not plc.outputs()[1])
    beat["on"] = False
    plc.stop()


# ── 2. The Modbus map ────────────────────────────────────────────

def modbus(port, pdu, tid=[0]):
    tid[0] += 1
    with socket.create_connection(("127.0.0.1", port), 2) as s:
        s.sendall(struct.pack(">HHHB", tid[0], 0, len(pdu) + 1, 1) + pdu)
        head = s.recv(7)
        length = struct.unpack(">H", head[4:6])[0]
        return s.recv(length - 1)


def test_modbus():
    print("-- Modbus TCP map")
    plc = Dvp12se()
    plc.start()
    srv = ModbusServer(plc, free_port()).serve_background()
    port = srv.server_address[1]
    values = [1, 2, 3]
    r = modbus(port, struct.pack(">BHHB", 16, D_BASE, 3, 6) + b"".join(struct.pack(">H", v) for v in values))
    check("FC16 at 4096 writes D0..D2", r[0] == 16 and [plc.d(i) for i in range(3)] == values, r)
    with plc.lock:
        plc.D[10] = 2
    r = modbus(port, struct.pack(">BHH", 3, D_BASE + 10, 10))
    check("FC3 at 4106 reads D10", r[0] == 3 and struct.unpack(">H", r[2:4])[0] == 2, r)
    r = modbus(port, struct.pack(">BHHB", 16, 0, 1, 2) + b"\x00\x01")
    check("Siemens address 0 is refused (exception 2)", r[:2] == b"\x90\x02", r)
    r = modbus(port, struct.pack(">BHH", 3, D_BASE, 101))
    check("more than 100 registers refused (exception 3)", r[:2] == b"\x83\x03", r)
    srv.shutdown()
    plc.stop()


# ── 3. Agent + billing server + DVP12SE ──────────────────────────

class Chain:
    def __init__(self, work):
        self.work = work
        self.db = os.path.join(work, "peralna.sqlite")
        self.env = dict(os.environ, PERALNA_DB=self.db)
        self.log = []
        self.agent = None

    def php(self, code):
        script = os.path.join(self.work, "q.php")
        with open(script, "w", encoding="utf-8") as f:
            f.write("<?php require %r; %s" % (os.path.join(ROOT, "app", "lib.php").replace("\\", "/"), code))
        return subprocess.run([PHP, script], env=self.env, capture_output=True, text=True, encoding="utf-8").stdout.strip()

    def start_agent(self, ini):
        self.agent = subprocess.Popen([os.path.join(self.work, "PeralnaAgent.exe"), ini], stdin=subprocess.PIPE,
                                      stdout=subprocess.PIPE, stderr=subprocess.STDOUT, cwd=self.work)

        def pump(p):
            for raw in p.stdout:
                self.log.append(raw.decode("utf-8", "replace").rstrip())
        threading.Thread(target=pump, args=(self.agent,), daemon=True).start()

    def say(self, line):
        self.agent.stdin.write((line + "\n").encode())
        self.agent.stdin.flush()


def test_chain():
    print("-- agent + billing + DVP12SE")
    work = tempfile.mkdtemp(prefix="peralna-delta-")
    c = Chain(work)
    exe = os.path.join(work, "PeralnaAgent.exe")
    build = subprocess.run([CSC, "/nologo", "/optimize", "/out:" + exe, "/r:System.Web.Extensions.dll",
                            os.path.join(ROOT, "agent", "PeralnaAgent.cs")], capture_output=True, text=True)
    check("agent compiles", build.returncode == 0, build.stdout + build.stderr)
    if build.returncode:
        return

    plc = Dvp12se()
    plc.start()
    srv = ModbusServer(plc, free_port()).serve_background()
    plc_port = srv.server_address[1]
    web_port = free_port()
    web = subprocess.Popen([PHP, "-S", "127.0.0.1:%d" % web_port, "-t", os.path.join(ROOT, "www")], env=c.env,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        errors = c.php("""echo json_encode(save_plc_settings(['plc_model' => 'dvp12se', 'plc_mode' => 'server',
            'plc_ip' => '127.0.0.1', 'plc_port' => '%d', 'plc_unit_id' => '1', 'plc_poll_ms' => '100', 'plc_d_start' => '0']));
            db()->prepare("INSERT INTO cards (uid, holder_name) VALUES (?, ?)")->execute(['%s', '%s']);
            apply_transaction((int)find_card_by_uid('%s')['id'], 'topup', 100, ['note' => 'test']);""" % (plc_port, CARD, HOLDER, CARD))
        check("Delta settings saved (mode forced to client, D0 = 4096)",
              errors == "[]" and c.php("echo setting('plc_mode'), '|', setting('plc_reg_base');") == "client|4096",
              errors)
        check("bad first D register refused",
              "D регистар" in c.php("echo implode(' ', save_plc_settings(['plc_model' => 'dvp12se', 'plc_ip' => '127.0.0.1', 'plc_port' => '502', 'plc_unit_id' => '1', 'plc_poll_ms' => '100', 'plc_d_start' => '5000']));"))
        check("first D register 300 refused (overlaps D300..D311)",
              "D регистар" in c.php("echo implode(' ', save_plc_settings(['plc_model' => 'dvp12se', 'plc_ip' => '127.0.0.1', 'plc_port' => '502', 'plc_unit_id' => '1', 'plc_poll_ms' => '100', 'plc_d_start' => '300']));"))

        ini = os.path.join(work, "agent.ini")
        with open(ini, "w", encoding="utf-8") as f:
            f.write("machine = B1\nserver = http://127.0.0.1:%d/api.php\nweb_port = 0\nsimulate = 1\n"
                    "tick_seconds = 5\nvirtual_reader_port = 0\nplc_model = dvp12se\nplc_ip = 127.0.0.1\n"
                    "plc_port = %d\n" % (web_port, plc_port))
        wait(lambda: subprocess.run([PHP, "-r", "echo @file_get_contents('http://127.0.0.1:%d/api.php?action=ping');" % web_port],
                                    capture_output=True, text=True).stdout.startswith("{"), 10, 0.3)
        c.start_agent(ini)

        h0 = plc.d(5)
        check("agent connects and sends a heartbeat", wait(lambda: srv.requests > 10 and plc.d(5) != h0, 8), c.log[-5:])
        check("admin panel sees the PLC as connected", wait(lambda: c.php("echo setting('plc_ok');") == "1", 6))
        link = c.php("echo setting('plc_link');")
        check("admin panel shows the Delta link", "Delta DVP12SE" in link and "D0–D52" in link, link)
        check("Y3 'no link' lamp off", not plc.outputs()[3])
        check("X3 open: D4 = 9 'machine not OK', even without a card", wait(lambda: plc.d(4) == 9, 3), plc.d(4))
        plc.press("X3")                 # machine OK chain closed
        check("X3 closed: D4 = 0 'insert card'", wait(lambda: plc.d(4) == 0, 3), plc.d(4))

        c.say("in DEADBEEF")
        check("unknown card: D4 = 4, D0 = 0", wait(lambda: plc.d(4) == 4 and plc.d(0) == 0, 4), (plc.d(4), plc.d(0)))
        c.say("out")
        wait(lambda: plc.d(4) == 0, 3)

        c.say("in " + CARD)
        check("card in: D0 = 1, D4 = 1, Y2 on", wait(lambda: plc.d(0) == 1 and plc.d(4) == 1 and plc.outputs()[2], 4),
              [plc.d(i) for i in range(7)])
        check("balance 100 in D2", plc.d(2) == 100, plc.d(2))
        check("300 s at 20 ден/мин in D1, 5 min in D6", plc.d(1) == 300 and plc.d(6) == 5, (plc.d(1), plc.d(6)))
        name = "".join(chr(plc.d(21 + i)) for i in range(plc.d(20)))
        check("Cyrillic card name in D20..D52", name == HOLDER, name)

        time.sleep(3)
        check("no foam/water yet: nothing charged, no seconds used", plc.d(3) == 0 and plc.d(1) == 300,
              (plc.d(3), plc.d(1)))

        plc.pulse("X0")
        check("ПЕНА: Y0 on", wait(lambda: plc.outputs()[0], 1))
        time.sleep(6.5)
        left = plc.d(1)
        check("seconds count down while foam runs", 290 <= left <= 295, left)
        plc.pulse("X2")
        check("СТОП: Y0 off", wait(lambda: not plc.outputs()[0], 1))
        # The agent tells the server at once; wait for that answer before reading D3.
        check("agent reports the pause", wait(lambda: any("STOP: billing paused" in l for l in c.log), 3))
        time.sleep(0.5)
        charged, left = plc.d(3), plc.d(1)
        check("charged for the foam (2-3 ден. for ~6.5 s)", 2 <= charged <= 3, charged)
        check("D1 = what the balance still buys", left == (100 - charged) * 3, (left, charged))
        time.sleep(6)
        check("СТОП pauses billing", plc.d(3) == charged and plc.d(1) == left, (plc.d(3), plc.d(1), charged, left))

        plc.pulse("X1")
        check("ВОДА: Y1 on", wait(lambda: plc.outputs()[1], 1))
        time.sleep(3)
        pauses = sum("STOP: billing paused" in l for l in c.log)
        plc.press("X3", False)          # motor protection trips while the water runs
        check("X3 opens: Y1 off at once", wait(lambda: not plc.outputs()[1], 1))
        check("X3 open: the agent shows status 9 in D4", wait(lambda: plc.d(4) == 9, 3), plc.d(4))
        check("X3 open: the agent logs it and pauses billing",
              wait(lambda: any("machine NOT OK" in l for l in c.log) and sum("STOP: billing paused" in l for l in c.log) > pauses, 3))
        time.sleep(0.5)
        charged = plc.d(3)
        time.sleep(4)
        check("X3 open: nothing is charged", plc.d(3) == charged, (plc.d(3), charged))
        check("admin panel shows the machine fault", c.php("echo setting('plc_fault');") == "1")
        plc.press("X3")
        check("X3 closed: D4 back to 1, water stays off", wait(lambda: plc.d(4) == 1, 3) and not plc.outputs()[1], plc.d(4))
        c.say("out")
        check("card out: D0 = 0, outputs off", wait(lambda: plc.d(0) == 0 and plc.outputs() == (False, False, False, False), 3),
              plc.outputs())
        check("card out: D10 back to 0", plc.d(10) == 0)
        row = c.php("$s = db()->query('SELECT status, charged FROM sessions ORDER BY id DESC LIMIT 1')->fetch();"
                    "echo $s['status'], '|', $s['charged'], '|', find_card_by_uid('%s')['balance'];" % CARD)
        status, total, balance = row.split("|")
        check("session closed and billed (foam + water, 3-5 ден.)", status == "ended" and 3 <= int(total) <= 5, row)
        check("card balance = 100 - charged", int(balance) == 100 - int(total), row)

        c.say("in " + CARD)
        wait(lambda: plc.d(0) == 1, 4)
        plc.pulse("X0")
        check("foam on again", wait(lambda: plc.outputs()[0], 1))
        c.agent.kill()
        t0 = time.time()
        check("agent killed: watchdog stops the foam", wait(lambda: not plc.outputs()[0], 8))
        # 5 s from the last heartbeat, which came up to 1 s before the kill.
        took = time.time() - t0
        check("watchdog after 4-7 s (%.1f s)" % took, 4.0 <= took <= 7)
        check("Y3 'no link' lamp on", plc.outputs()[3])
    finally:
        if c.agent and c.agent.poll() is None:
            c.agent.kill()
        web.kill()
        srv.shutdown()
        plc.stop()
    if failures:
        print("\n".join(c.log[-30:]))


if __name__ == "__main__":
    test_program()
    test_modbus()
    test_chain()
    print("\n%s" % ("ALL OK" if not failures else "%d FAILED" % failures))
    sys.exit(1 if failures else 0)
