<?php
function page_header(string $title, ?array $user = null, bool $autoRefresh = false): void
{
    $page = basename($_SERVER['SCRIPT_NAME']);
    $nav = [
        'index.php' => 'Машини',
        'cards.php' => 'Картички',
        'sessions.php' => 'Перења',
        'machines.php' => 'Цени',
    ];
    ?><!doctype html>
<html lang="mk">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<?php if ($autoRefresh): ?><meta http-equiv="refresh" content="5"><?php endif; ?>
<title><?= e($title) ?> · <?= e(APP_NAME) ?></title>
<link rel="icon" href="favicon.ico">
<link rel="apple-touch-icon" href="assets/icon-512.png">
<link rel="stylesheet" href="assets/style.css?v=<?= filemtime(dirname(__DIR__) . '/www/assets/style.css') ?>">
</head>
<body>
<?php if ($user): ?>
<header class="topbar">
  <a class="brand" href="index.php"><img src="assets/icon-512.png" alt="" class="logo"> <?= e(APP_NAME) ?></a>
  <nav>
    <?php foreach ($nav as $href => $label): ?>
      <a href="<?= $href ?>" class="<?= $page === $href || ($page === 'card.php' && $href === 'cards.php') ? 'active' : '' ?>"><?= e($label) ?></a>
    <?php endforeach; ?>
  </nav>
  <form method="post" action="logout.php" class="logout"><?= csrf_field() ?><button class="link"><?= e($user['username']) ?> · Одјава</button></form>
</header>
<?php endif; ?>
<main>
<?php if ($f = flash()): ?><div class="flash <?= e($f['kind']) ?>"><?= e($f['message']) ?></div><?php endif; ?>
<?php
}

function page_footer(): void
{
    echo "</main>\n</body>\n</html>\n";
}
