<?php
declare(strict_types=1);
require dirname(__DIR__) . '/app/lib.php';
start_php_session();
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    $_SESSION = [];
    session_destroy();
}
redirect('login.php');
