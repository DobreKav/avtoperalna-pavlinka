-- SQLite. Amounts are whole denars; timestamps are UTC 'YYYY-MM-DD HH:MM:SS'.

CREATE TABLE IF NOT EXISTS users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT NOT NULL UNIQUE,
    password_hash TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS cards (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    uid TEXT NOT NULL UNIQUE,
    holder_name TEXT NOT NULL DEFAULT '',
    phone TEXT NOT NULL DEFAULT '',
    balance INTEGER NOT NULL DEFAULT 0 CHECK (balance >= 0),
    status TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'blocked')),
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- price_per_minute is billed while the card stays in the machine's reader.
-- min_balance is what the card needs to start the machine at all.
CREATE TABLE IF NOT EXISTS machines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    type TEXT NOT NULL DEFAULT 'washer' CHECK (type IN ('washer', 'dryer')),
    price_per_minute INTEGER NOT NULL CHECK (price_per_minute > 0),
    min_balance INTEGER NOT NULL DEFAULT 0 CHECK (min_balance >= 0),
    active INTEGER NOT NULL DEFAULT 1
);

-- One row per card-in-reader period. charged grows while the card is inside;
-- the balance on the card is reduced at the same time.
CREATE TABLE IF NOT EXISTS sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    card_id INTEGER NOT NULL REFERENCES cards(id),
    machine_id INTEGER NOT NULL REFERENCES machines(id),
    rate_per_minute INTEGER NOT NULL,
    started_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    ended_at TEXT NULL,
    charged INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'ended')),
    end_reason TEXT NULL CHECK (end_reason IN ('removed', 'no_balance', 'timeout', 'admin'))
);
CREATE UNIQUE INDEX IF NOT EXISTS one_running_per_machine ON sessions(machine_id) WHERE status = 'running';
CREATE UNIQUE INDEX IF NOT EXISTS one_running_per_card ON sessions(card_id) WHERE status = 'running';
CREATE INDEX IF NOT EXISTS sessions_card ON sessions(card_id, started_at);
CREATE INDEX IF NOT EXISTS sessions_started ON sessions(started_at);

-- The ledger. A machine session is written here once, when the card comes out.
CREATE TABLE IF NOT EXISTS transactions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    card_id INTEGER NOT NULL REFERENCES cards(id),
    type TEXT NOT NULL CHECK (type IN ('topup', 'charge', 'refund', 'adjust')),
    amount INTEGER NOT NULL,
    balance_after INTEGER NOT NULL,
    session_id INTEGER NULL REFERENCES sessions(id),
    machine_id INTEGER NULL REFERENCES machines(id),
    note TEXT NOT NULL DEFAULT '',
    source TEXT NOT NULL DEFAULT 'admin' CHECK (source IN ('admin', 'terminal', 'api')),
    user_id INTEGER NULL REFERENCES users(id),
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS transactions_card ON transactions(card_id, created_at);
CREATE INDEX IF NOT EXISTS transactions_created ON transactions(created_at);

CREATE TABLE IF NOT EXISTS settings (
    name TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

INSERT OR IGNORE INTO machines (code, name, type, price_per_minute, min_balance) VALUES
    ('B1', 'Бокс 1 — пена и вода', 'washer', 20, 20);
