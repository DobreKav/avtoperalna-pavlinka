<?php
declare(strict_types=1);
require dirname(__DIR__) . '/app/lib.php';
require dirname(__DIR__) . '/app/layout.php';
start_php_session();

try {
    if ((int)db()->query('SELECT COUNT(*) FROM users')->fetchColumn() === 0) redirect('setup.php');
} catch (PDOException $err) {
    redirect('setup.php');
}
if (current_user()) redirect('index.php');

$error = '';
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    $stmt = db()->prepare('SELECT id, username, password_hash FROM users WHERE username = ?');
    $stmt->execute([trim((string)($_POST['username'] ?? ''))]);
    $user = $stmt->fetch();
    if ($user && password_verify((string)($_POST['password'] ?? ''), $user['password_hash'])) {
        session_regenerate_id(true);
        $_SESSION['user'] = ['id' => (int)$user['id'], 'username' => $user['username']];
        redirect('index.php');
    }
    usleep(400000);
    $error = 'Погрешно корисничко име или лозинка.';
}

page_header('Најава');
?>
<section class="card narrow">
  <h1>🧺 Најава</h1>
  <?php if ($error): ?><div class="flash error"><?= e($error) ?></div><?php endif; ?>
  <form method="post" class="stack">
    <?= csrf_field() ?>
    <label>Корисничко име <input name="username" required autofocus></label>
    <label>Лозинка <input type="password" name="password" required></label>
    <button class="primary">Најави се</button>
  </form>
</section>
<?php page_footer();
