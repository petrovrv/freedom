<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Server side of the CO sign-in (TZ-CO-WEB §5.1, v1.2): Google authorization code flow.
// The refresh token never reaches JavaScript: it lives AES-256-GCM encrypted in an HttpOnly cookie;
// the key is in ~/co-auth/config.php outside public_html. The server keeps no token store.
declare(strict_types=1);

const CO_SESSION_COOKIE = '__Host-co_session';
const CO_FLOW_COOKIE = '__Host-co_oauth';
const CO_SESSION_MAX_AGE = 34560000; // 400 days, the browser maximum

function co_config(): array {
    static $cfg = null;
    if ($cfg === null) {
        // Freedom F2: ~/co-auth above the web root - hPanel ~/domains/<site>/public_html (depth 4), cPanel ~/public_html
        $file = dirname(__DIR__, 4) . '/co-auth/config.php';
        foreach ([4, 2, 3, 5] as $up) {
            if (@is_file(dirname(__DIR__, $up) . '/co-auth/config.php')) {
                $file = dirname(__DIR__, $up) . '/co-auth/config.php';
                break;
            }
        }
        $cfg = require $file;
    }
    return $cfg;
}

function co_b64url(string $b): string { return rtrim(strtr(base64_encode($b), '+/', '-_'), '='); }
function co_unb64url(string $s): string { return (string)base64_decode(strtr($s, '-_', '+/'), true); }

function co_seal(array $data): string {
    $key = base64_decode(co_config()['cookie_key']);
    $iv = random_bytes(12);
    $tag = '';
    $ct = openssl_encrypt(json_encode($data), 'aes-256-gcm', $key, OPENSSL_RAW_DATA, $iv, $tag, 'co.v1');
    return co_b64url($iv . $tag . $ct);
}

function co_open(?string $sealed): ?array {
    if (!$sealed) return null;
    $raw = co_unb64url($sealed);
    if (strlen($raw) < 29) return null;
    $key = base64_decode(co_config()['cookie_key']);
    $pt = openssl_decrypt(substr($raw, 28), 'aes-256-gcm', $key, OPENSSL_RAW_DATA, substr($raw, 0, 12), substr($raw, 12, 16), 'co.v1');
    if ($pt === false) return null;
    $d = json_decode($pt, true);
    return is_array($d) ? $d : null;
}

function co_set_cookie(string $name, string $value, int $maxAge, string $sameSite): void {
    setcookie($name, $value, [
        'expires' => $maxAge > 0 ? time() + $maxAge : time() - 3600,
        'path' => '/', 'secure' => true, 'httponly' => true, 'samesite' => $sameSite,
    ]);
}

function co_json(int $status, array $body): never {
    http_response_code($status);
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    echo json_encode($body);
    exit;
}

// Same-origin XHR only: a custom header cannot be sent cross-site without a CORS preflight we never answer.
function co_require_api_call(): void {
    if ($_SERVER['REQUEST_METHOD'] !== 'POST' || ($_SERVER['HTTP_X_CO_AUTH'] ?? '') !== '1') {
        co_json(405, ['error' => 'method_not_allowed']);
    }
    $origin = $_SERVER['HTTP_ORIGIN'] ?? '';
    if ($origin !== '' && $origin !== co_config()['origin']) co_json(403, ['error' => 'bad_origin']);
}

function co_post_form(string $url, array $fields): array {
    $ch = curl_init($url);
    curl_setopt_array($ch, [
        CURLOPT_POST => true, CURLOPT_POSTFIELDS => http_build_query($fields),
        CURLOPT_RETURNTRANSFER => true, CURLOPT_TIMEOUT => 20,
        CURLOPT_HTTPHEADER => ['Content-Type: application/x-www-form-urlencoded'],
    ]);
    $body = curl_exec($ch);
    $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);
    $data = is_string($body) ? json_decode($body, true) : null;
    return [$status, is_array($data) ? $data : []];
}

function co_page(string $title, string $text): never {
    header('Content-Type: text/html; charset=utf-8');
    header('Cache-Control: no-store');
    echo '<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width">'
       . '<title>CO</title><body style="font-family:system-ui;max-width:32rem;margin:4rem auto;padding:0 1rem">'
       . '<h1 style="font-size:1.3rem">' . htmlspecialchars($title) . '</h1><p>' . htmlspecialchars($text)
       . '</p><p><a href="/">Back to CO</a></p></body>';
    exit;
}
