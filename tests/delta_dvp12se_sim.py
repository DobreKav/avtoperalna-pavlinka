"""Delta DVP12SE emulator for tests: Modbus TCP server with the DVP12SE address map that runs
the instruction list from plc/Delta_DVP12SE.txt every scan.

    python tests/delta_dvp12se_sim.py [port]        standalone, type "press X0" / "release X0" / "show"

Modbus map (as on the real DVP12SE):
    D0..D4095   holding registers 16#1000..16#1FFF (4096..8191)   FC3, FC6, FC16
    M0..M1535   coils 16#0800..                                   FC1, FC5, FC15
    X0..X7      16#0400..   Y0..Y3  16#0500..                       FC1/FC2 read
Anything else is refused with exception 2, at most 100 registers per request (exception 3).
Only the instructions the bay program uses are implemented.
"""
import os
import socketserver
import struct
import sys
import threading
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROGRAM = os.path.join(ROOT, "plc", "Delta_DVP12SE.txt")

D_BASE, M_BASE, X_BASE, Y_BASE = 0x1000, 0x0800, 0x0400, 0x0500
MAX_REGS = 100


def s16(v):
    v &= 0xFFFF
    return v - 0x10000 if v & 0x8000 else v


def load_program(path=PROGRAM):
    lines, inside = [], False
    with open(path, encoding="utf-8") as f:
        for raw in f:
            line = raw.strip()
            if line == "----- BEGIN IL -----":
                inside = True
            elif line == "----- END IL -----":
                inside = False
            elif inside and line:
                parts = line.split()
                lines.append((parts[0].upper(), parts[1:]))
    if not lines or lines[-1][0] != "END":
        raise ValueError("program must end with END")
    return lines


class Dvp12se:
    def __init__(self, program=None, scan_ms=10):
        self.program = program or load_program()
        self.scan_ms = scan_ms
        self.lock = threading.Lock()
        self.D = [0] * 10000
        self.M = [False] * 4096
        self.X = [False] * 8
        self.Y = [False] * 4
        self.T = [False] * 256
        self.t_acc = [0.0] * 256      # timer value in ms
        self.first_scan = True
        self.running = False
        self.last_scan = None
        self.scans = 0

    # ── devices ──────────────────────────────────────────────
    def _bit(self, name):
        kind, n = name[0], name[1:]
        if kind == "X": return self.X[int(n, 8)]
        if kind == "Y": return self.Y[int(n, 8)]
        if kind == "T": return self.T[int(n)]
        if kind == "M":
            n = int(n)
            if n == 1000: return True                 # normally ON while RUN
            if n == 1002: return self.first_scan      # ON for the first scan only
            return self.M[n]
        raise ValueError("bit device " + name)

    def _set_bit(self, name, value):
        kind, n = name[0], name[1:]
        if kind == "Y": self.Y[int(n, 8)] = value
        elif kind == "M": self.M[int(n)] = value
        else: raise ValueError("cannot write " + name)

    def _word(self, name):
        if name[0] == "K": return int(name[1:])
        if name[0] == "D": return s16(self.D[int(name[1:])])
        raise ValueError("word device " + name)

    def _set_word(self, name, value):
        if name[0] != "D": raise ValueError("cannot write " + name)
        self.D[int(name[1:])] = value & 0xFFFF

    # ── one PLC scan ─────────────────────────────────────────
    def scan(self, dt_ms):
        stack, acc = [], False
        cmp_ops = {"=": lambda a, b: a == b, "<>": lambda a, b: a != b, ">": lambda a, b: a > b,
                   ">=": lambda a, b: a >= b, "<": lambda a, b: a < b, "<=": lambda a, b: a <= b}
        for op, args in self.program:
            if op == "END":
                break
            for prefix in ("LD", "AND", "OR"):
                rest = op[len(prefix):]
                if op.startswith(prefix) and rest in cmp_ops:
                    value = cmp_ops[rest](self._word(args[0]), self._word(args[1]))
                    op, args = prefix + "#", [value]
                    break
            if op == "LD": stack.append(acc); acc = self._bit(args[0])
            elif op == "LDI": stack.append(acc); acc = not self._bit(args[0])
            elif op == "LD#": stack.append(acc); acc = args[0]
            elif op == "AND": acc = acc and self._bit(args[0])
            elif op == "ANI": acc = acc and not self._bit(args[0])
            elif op == "AND#": acc = acc and args[0]
            elif op == "OR": acc = acc or self._bit(args[0])
            elif op == "ORI": acc = acc or not self._bit(args[0])
            elif op == "OR#": acc = acc or args[0]
            elif op == "ANB": acc = stack.pop() and acc
            elif op == "ORB": acc = stack.pop() or acc
            elif op == "OUT": self._set_bit(args[0], acc)
            elif op == "SET":
                if acc: self._set_bit(args[0], True)
            elif op == "RST":
                if acc: self._set_bit(args[0], False)
            elif op == "MOV":
                if acc: self._set_word(args[1], self._word(args[0]))
            elif op == "CMP":
                if acc:
                    a, b = self._word(args[0]), self._word(args[1])
                    base = int(args[2][1:])
                    self.M[base], self.M[base + 1], self.M[base + 2] = a > b, a == b, a < b
            elif op == "DIV":
                if acc:
                    a, b = self._word(args[0]), self._word(args[1])
                    q = int(a / b)
                    n = int(args[2][1:])
                    self.D[n], self.D[n + 1] = q & 0xFFFF, (a - q * b) & 0xFFFF
            elif op == "TMR":
                n, preset = int(args[0][1:]), self._word(args[1])
                if acc:
                    self.t_acc[n] += dt_ms
                    self.T[n] = self.t_acc[n] >= preset * 100   # T0..T199: 100 ms base
                else:
                    self.t_acc[n], self.T[n] = 0.0, False
            else:
                raise ValueError("instruction not emulated: " + op)
        self.first_scan = False
        self.scans += 1

    def start(self):
        self.running = True
        threading.Thread(target=self._loop, daemon=True).start()

    def stop(self):
        self.running = False

    def _loop(self):
        self.last_scan = time.monotonic()
        while self.running:
            time.sleep(self.scan_ms / 1000)
            now = time.monotonic()
            with self.lock:
                self.scan((now - self.last_scan) * 1000)
            self.last_scan = now

    # ── test helpers ─────────────────────────────────────────
    def press(self, inp, down=True):
        with self.lock:
            self.X[int(inp[1:], 8)] = down

    def pulse(self, inp, seconds=0.2):
        self.press(inp, True)
        time.sleep(seconds)
        self.press(inp, False)

    def d(self, n):
        with self.lock:
            return self.D[n]

    def outputs(self):
        with self.lock:
            return tuple(self.Y)

    # ── Modbus PDU handling ──────────────────────────────────
    def handle(self, pdu):
        fc = pdu[0]
        fail = lambda code: bytes([fc | 0x80, code])
        if fc in (1, 2, 3, 4, 5, 6, 15, 16) and len(pdu) < 5:
            return fail(3)
        if fc not in (1, 2, 3, 5, 6, 15, 16):
            return fail(1)
        addr, qty = struct.unpack(">HH", pdu[1:5])
        with self.lock:
            if fc == 3:
                if not 1 <= qty <= MAX_REGS: return fail(3)
                if not (D_BASE <= addr and addr + qty <= D_BASE + 4096): return fail(2)
                values = self.D[addr - D_BASE:addr - D_BASE + qty]
                return bytes([3, qty * 2]) + b"".join(struct.pack(">H", v) for v in values)
            if fc == 6:
                if not D_BASE <= addr < D_BASE + 4096: return fail(2)
                self.D[addr - D_BASE] = qty
                return pdu[:5]
            if fc == 16:
                if not 1 <= qty <= MAX_REGS or len(pdu) < 6 + qty * 2: return fail(3)
                if not (D_BASE <= addr and addr + qty <= D_BASE + 4096): return fail(2)
                for i in range(qty):
                    self.D[addr - D_BASE + i] = struct.unpack(">H", pdu[6 + i * 2:8 + i * 2])[0]
                return pdu[:5]
            if fc in (1, 2):
                if M_BASE <= addr and addr + qty <= M_BASE + 1536: bits = self.M[addr - M_BASE:addr - M_BASE + qty]
                elif X_BASE <= addr and addr + qty <= X_BASE + 8: bits = self.X[addr - X_BASE:addr - X_BASE + qty]
                elif Y_BASE <= addr and addr + qty <= Y_BASE + 4: bits = self.Y[addr - Y_BASE:addr - Y_BASE + qty]
                else: return fail(2)
                out = bytearray((qty + 7) // 8)
                for i, b in enumerate(bits):
                    if b: out[i // 8] |= 1 << (i % 8)
                return bytes([fc, len(out)]) + bytes(out)
            if fc == 5:
                if not M_BASE <= addr < M_BASE + 1536: return fail(2)
                self.M[addr - M_BASE] = qty == 0xFF00
                return pdu[:5]
            if fc == 15:
                if not (M_BASE <= addr and addr + qty <= M_BASE + 1536): return fail(2)
                for i in range(qty):
                    self.M[addr - M_BASE + i] = bool(pdu[6 + i // 8] >> (i % 8) & 1)
                return pdu[:5]


class _Handler(socketserver.BaseRequestHandler):
    def handle(self):
        plc = self.server.plc
        self.server.connections += 1
        sock = self.request
        try:
            while True:
                header = _read(sock, 7)
                if header is None: return
                tid, proto, length, unit = struct.unpack(">HHHB", header)
                pdu = _read(sock, length - 1)
                if pdu is None: return
                self.server.requests += 1
                reply = plc.handle(pdu)
                sock.sendall(struct.pack(">HHHB", tid, 0, len(reply) + 1, unit) + reply)
        except (ConnectionError, OSError):
            return


def _read(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk: return None
        buf += chunk
    return buf


class ModbusServer(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, plc, port=502, host="127.0.0.1"):
        super().__init__((host, port), _Handler)
        self.plc = plc
        self.connections = 0
        self.requests = 0

    def serve_background(self):
        threading.Thread(target=self.serve_forever, daemon=True).start()
        return self


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 502
    plc = Dvp12se()
    plc.start()
    ModbusServer(plc, port, "0.0.0.0").serve_background()
    print("Delta DVP12SE emulator on port %d. Commands: press X0 | release X0 | show | quit" % port)
    for line in sys.stdin:
        cmd = line.split()
        if not cmd: continue
        if cmd[0] == "quit": break
        if cmd[0] in ("press", "release") and len(cmd) == 2: plc.press(cmd[1].upper(), cmd[0] == "press")
        print("D0-6=%s D10=%d Y0-3=%s" % ([plc.d(i) for i in range(7)], plc.d(10), plc.outputs()))
