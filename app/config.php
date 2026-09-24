<?php
// Local settings. The business name is fixed in lib.php (APP_NAME).
return [
    // SQLite database file. Back this file up; it holds every card and payment.
    'db_path' => getenv('PERALNA_DB') ?: dirname(__DIR__) . '/data/peralna.sqlite',
    'currency' => 'ден.',
    'timezone' => 'Europe/Skopje',
    // Only needed if the reader agent runs on another computer. With an empty key
    // the API answers this computer only.
    'api_key' => '',
    // A reader reports every ~15 s while the card is inside. If nothing arrives
    // for this long (power or network loss), the session is closed and billed
    // up to the last report.
    'session_timeout_seconds' => 120,
    // Fixed top-up buttons on the card page.
    'topup_presets' => [100, 200, 500, 1000],
];
