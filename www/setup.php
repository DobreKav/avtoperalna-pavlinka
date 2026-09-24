<?php
// First run: creates the tables and the first admin account.
// Locks itself once an admin exists.
declare(strict_types=1);
require dirname(__DIR__) . '/app/lib.php';
require dirname(__DIR__) . '/app/layout.php';
start_php_session();

$error = '';
try {
    // db() creates the tables on first use.
    $hasAdmin = (int)db()->query('SELECT COUNT(*) FROM users')->fetchColumn() > 0;
} catch (Throwable $err) {
    $hasAdmin = false;
    $error = 'Нема врска со базата: ' . $err->getMessage();
}

if ($hasAdmin) redirect('login.php');

if (!$error && $_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    $username = trim((string)($_POST['username'] ?? ''));
    $password = (string)($_POST['password'] ?? '');
    if (!preg_match('/^[\p{L}0-9._-]{3,60}$/u', $username)) {
        $error = 'Корисничкото име треба да има 3–60 букви или бројки.';
    } elseif (strlen($password) < 8) {
        $error = 'Лозинката треба да има најмалку 8 знаци.';
    } elseif ($password !== ($_POST['password2'] ?? '')) {
        $error = 'Лозинките не се исти.';
    } else {
        db()->prepare('INSERT INTO users (username, password_hash) VALUES (?, ?)')
            ->execute([$username, password_hash($password, PASSWORD_DEFAULT)]);
        flash('Админот е креиран. Најави се.');
        redirect('login.php');
    }
}

page_header('Почетно поставување');
?>
<section class="card narrow">
  <h1>Почетно поставување</h1>
  <p class="muted">Табелите се креирани. Направи го првиот админ.</p>
  <?php if ($error): ?><div class="flash error"><?= e($error) ?></div><?php endif; ?>
  <form method="post" class="stack">
    <?= csrf_field() ?>
    <label>Корисничко име <input name="username" required autofocus value="<?= e($_POST['username'] ?? 'admin') ?>"></label>
    <label>Лозинка <input type="password" name="password" required minlength="8"></label>
    <label>Повтори лозинка <input type="password" name="password2" required minlength="8"></label>
    <button class="primary">Креирај админ</button>
  </form>
</section>
<?php page_footer();
