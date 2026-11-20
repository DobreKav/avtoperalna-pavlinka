<?php
// PLC link settings: how the laptop and the S7-1200 talk Modbus TCP.
// Saved in the database; the agent picks them up within a few seconds (no restart).
declare(strict_types=1);
require dirname(__DIR__) . '/app/lib.php';
require dirname(__DIR__) . '/app/layout.php';
$user = require_login();

function link_status(): array
{
    $agentSeen = setting('agent_seen_at');
    $statusAt = setting('status_at');
    $fresh = $agentSeen && $statusAt && time() - utc_ts($agentSeen) < 20 && time() - utc_ts($statusAt) < 20;
    return [
        'fresh' => (bool)$fresh,
        'ok' => $fresh && setting('plc_ok') === '1',
        'peer' => (string)setting('plc_peer'),
        'link' => (string)setting('plc_link'),
        'error' => (string)setting('plc_error'),
        'local_ips' => array_values(array_filter(explode(',', (string)setting('local_ips')))),
        'version' => (string)setting('agent_version'),
    ];
}

// Polled by the page every 2 s so the status is live without reloading the form.
if (($_GET['status'] ?? '') === '1') {
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    echo json_encode(link_status(), JSON_UNESCAPED_UNICODE);
    exit;
}

$form = plc_settings();
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    check_csrf();
    $errors = save_plc_settings($_POST);
    if (!$errors) {
        flash('Поставките се зачувани. Агентот ги применува за неколку секунди.');
        redirect('plc.php');
    }
    flash(implode(' ', $errors), 'error');
    $form = array_merge($form, array_intersect_key($_POST, PLC_DEFAULTS));
    $form['test_mode'] = !empty($_POST['test_mode']) ? '1' : '0';
}
$isDelta = $form['plc_model'] === 'dvp12se';
// The Delta block is typed as its first D register; stored as a Modbus address (D0 = 4096).
$dStart = isset($_POST['plc_d_start']) ? (string)$_POST['plc_d_start']
    : (string)($isDelta ? max(0, (int)$form['plc_reg_base'] - DELTA_D0) : 0);

$st = link_status();
$ips = $st['local_ips'];
$laptopIp = $ips[0] ?? '192.168.1.10';
page_header('PLC врска', $user);
?>
<h1>PLC врска (Modbus TCP)</h1>

<section class="card plc-live <?= $st['ok'] ? 'on' : 'off' ?>" id="plcLive">
  <div class="row between">
    <div>
      <b id="liveTitle"><?= !$st['fresh'] ? 'Агентот не се јавува' : ($st['ok'] ? 'PLC е поврзан' : 'PLC не е поврзан') ?></b>
      <div class="muted" id="liveLink"><?= e($st['link']) ?><?= $st['ok'] && $st['peer'] ? ' · ' . e($st['peer']) : '' ?></div>
      <div class="plc-error" id="liveError"><?= $st['fresh'] && !$st['ok'] ? e($st['error']) : '' ?></div>
    </div>
    <div class="muted small" id="liveIps"><?= $ips ? 'IP на овој лаптоп: ' . e(implode(', ', $ips)) : '' ?></div>
  </div>
</section>

<form method="post" class="card plc-form">
  <?= csrf_field() ?>
  <h2>Кое PLC?</h2>
  <div class="mode-pick">
    <label class="mode-option">
      <input type="radio" name="plc_model" value="s7-1200" <?= !$isDelta ? 'checked' : '' ?>>
      <span><b>Siemens S7-1200</b><small>Програми во TIA Portal: <code>plc\FB_Peralna.scl</code> или <code>plc\FB_Peralna_Server.scl</code>.</small></span>
    </label>
    <label class="mode-option">
      <input type="radio" name="plc_model" value="dvp12se" <?= $isDelta ? 'checked' : '' ?>>
      <span><b>Delta DVP12SE</b><small>Вградениот Ethernet е Modbus TCP сервер, лаптопот се поврзува на него. Програма за ISPSoft / WPLSoft: <code>plc\Delta_DVP12SE.txt</code>.</small></span>
    </label>
  </div>

  <div class="siemens-only">
  <h2>Кој кого повикува?</h2>
  <div class="mode-pick">
    <label class="mode-option">
      <input type="radio" name="plc_mode" value="server" <?= $form['plc_mode'] !== 'client' ? 'checked' : '' ?>>
      <span><b>Лаптопот е сервер</b><small>PLC-то се поврзува на лаптопот (MB_CLIENT). Програма: <code>plc\FB_Peralna.scl</code>. IP адресата на лаптопот се пишува во PLC програмата.</small></span>
    </label>
    <label class="mode-option">
      <input type="radio" name="plc_mode" value="client" <?= $form['plc_mode'] === 'client' ? 'checked' : '' ?>>
      <span><b>PLC-то е сервер</b><small>Лаптопот се поврзува на PLC-то (MB_SERVER). Програма: <code>plc\FB_Peralna_Server.scl</code>. Во PLC програмата не се пишува ништо за лаптопот.</small></span>
    </label>
  </div>

  <fieldset class="mode-fields" data-mode="server">
    <legend>Лаптопот е сервер</legend>
    <div class="grid">
      <label>IP на која слуша лаптопот
        <input name="plc_listen_ip" value="<?= e($form['plc_listen_ip']) ?>" placeholder="0.0.0.0" inputmode="decimal">
        <small>0.0.0.0 = на сите мрежни картички</small>
      </label>
      <label>Порта
        <input name="plc_listen_port" value="<?= e($form['plc_listen_port']) ?>" placeholder="502" inputmode="numeric">
        <small>Стандардно 502</small>
      </label>
      <label>Дозволена IP на PLC-то
        <input name="plc_allowed_ip" value="<?= e($form['plc_allowed_ip']) ?>" placeholder="празно = било која" inputmode="decimal">
        <small>Ако е пополнета, само тоа PLC смее да се поврзе</small>
      </label>
    </div>
    <p class="muted">Во <code>FB_Peralna.scl</code> внеси ја IP адресата на лаптопот (<b><?= e($laptopIp) ?></b>) во четирите <code>ADDR</code> линии и портата во <code>RemotePort</code>.</p>
  </fieldset>
  </div>

  <fieldset class="mode-fields" data-mode="client">
    <legend><span class="siemens-only">PLC-то е сервер</span><span class="delta-only">Delta DVP12SE (Modbus TCP сервер)</span></legend>
    <div class="grid">
      <label>IP на PLC-то
        <input name="plc_ip" value="<?= e($form['plc_ip']) ?>" placeholder="192.168.1.20" inputmode="decimal">
        <small class="siemens-only">Од TIA Portal → PROFINET интерфејс</small>
        <small class="delta-only">Од ISPSoft / DCISoft → Ethernet поставки (фабрички често 192.168.1.5)</small>
      </label>
      <label>Порта на PLC-то
        <input name="plc_port" value="<?= e($form['plc_port']) ?>" placeholder="502" inputmode="numeric">
        <small class="siemens-only">Иста како <code>LocalPort</code> во програмата</small>
        <small class="delta-only">Modbus TCP на DVP12SE е 502</small>
      </label>
      <label>Unit ID
        <input name="plc_unit_id" value="<?= e($form['plc_unit_id']) ?>" placeholder="1" inputmode="numeric">
        <small class="siemens-only">S7-1200 MB_SERVER прифаќа било кој</small>
        <small class="delta-only">За DVP12SE остави 1</small>
      </label>
      <label>Освежување (ms)
        <input name="plc_poll_ms" value="<?= e($form['plc_poll_ms']) ?>" placeholder="200" inputmode="numeric">
        <small>50–2000, стандардно 200</small>
      </label>
      <label class="delta-only">Почетен D регистар
        <input name="plc_d_start" value="<?= e($dStart) ?>" placeholder="0" inputmode="numeric">
        <small>0 = D0…D52 (Modbus 4096…4148). Мора да е ист како во PLC програмата.</small>
      </label>
    </div>
    <p class="muted delta-only">Во DVP12SE не се пишува ништо за лаптопот. Програмата <code>plc\Delta_DVP12SE.txt</code> ги користи D0–D52, копчињата X0 ПЕНА, X1 ВОДА, X2 СТОП и излезите Y0 пена, Y1 вода, Y2 подготвено.</p>
  </fieldset>

  <h2>Тест режим</h2>
  <label class="check">
    <input type="checkbox" name="test_mode" value="1" <?= $form['test_mode'] === '1' ? 'checked' : '' ?>>
    Вклучи ги емулаторите и демо картичката (само за проба, не во работа)
  </label>
  <p class="muted">Исклучено: агентот работи само со вистинскиот читач и вистинското PLC. Демо картичката <code><?= e(DEMO_UID) ?></code> се одбива.</p>

  <button class="primary">Зачувај</button>
</form>

<script>
(function () {
  var radios = document.querySelectorAll('input[name="plc_mode"], input[name="plc_model"]');
  function show() {
    var delta = document.querySelector('input[name="plc_model"]:checked').value === 'dvp12se';
    // The DVP12SE is always the server: the laptop connects to it.
    var mode = delta ? 'client' : document.querySelector('input[name="plc_mode"]:checked').value;
    document.querySelectorAll('.mode-fields').forEach(function (f) { f.hidden = f.dataset.mode !== mode; });
    document.querySelectorAll('.delta-only').forEach(function (el) { el.hidden = !delta; });
    document.querySelectorAll('.siemens-only').forEach(function (el) { el.hidden = delta; });
  }
  radios.forEach(function (r) { r.addEventListener('change', show); });
  show();

  var box = document.getElementById('plcLive');
  function poll() {
    fetch('plc.php?status=1', { cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (s) {
      box.className = 'card plc-live ' + (s.ok ? 'on' : 'off');
      document.getElementById('liveTitle').textContent = !s.fresh ? 'Агентот не се јавува' : (s.ok ? 'PLC е поврзан' : 'PLC не е поврзан');
      document.getElementById('liveLink').textContent = s.link + (s.ok && s.peer ? ' · ' + s.peer : '');
      document.getElementById('liveError').textContent = s.fresh && !s.ok ? s.error : '';
      document.getElementById('liveIps').textContent = s.local_ips.length ? 'IP на овој лаптоп: ' + s.local_ips.join(', ') : '';
    }).catch(function () {});
  }
  setInterval(poll, 2000);
})();
</script>
<?php page_footer();
