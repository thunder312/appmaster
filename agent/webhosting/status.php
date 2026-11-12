<?php
/**
 * Appmaster-Status für Webhosting (nur lesend).
 *
 * Auf Shared-Webhosting kann Appmaster nichts starten oder stoppen – hier geht nur: Speicherplatz, Last,
 * PHP-Version. Setze unten TOKEN, lade die Datei z. B. nach /appmaster/status.php hoch und trage in
 * Appmaster beim Rechner „status.php (Webhosting)“, die volle Adresse und denselben Token ein.
 * Nur über HTTPS betreiben, sonst ist der Token mitlesbar.
 */
const TOKEN = 'HIER-LANGEN-ZUFALLSWERT-EINTRAGEN';   // mindestens 16 Zeichen

header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

$gesendet = $_SERVER['HTTP_X_APPMASTER_TOKEN'] ?? '';
if (strlen(TOKEN) < 16 || TOKEN === 'HIER-LANGEN-ZUFALLSWERT-EINTRAGEN' || !hash_equals(TOKEN, $gesendet)) {
    http_response_code(401);
    echo json_encode(['fehler' => 'Token fehlt oder ist falsch']);
    exit;
}

$daten = [
    'dienst' => 'appmaster-status-php',
    'version' => '0.1.0',
    'read_only' => true,
    'php' => PHP_VERSION,
];

if (function_exists('sys_getloadavg')) {
    $last = @sys_getloadavg();
    if ($last) { $daten['load'] = $last; }
}

$pfad = $_SERVER['DOCUMENT_ROOT'] ?? __DIR__;
$gesamt = @disk_total_space($pfad);
$frei = @disk_free_space($pfad);
if ($gesamt && $frei !== false) {
    $daten['disks'] = [[
        'pfad' => 'Webspace',
        'total_gb' => round($gesamt / 1e9, 1),
        'used_gb' => round(($gesamt - $frei) / 1e9, 1),
    ]];
}

echo json_encode($daten);
