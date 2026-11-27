<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// Chat protocol (schema 8): validation and application of the secretary's operations. The model's answer is never
// trusted: the source message must be in the batch, quotes of decisions/constraints/facts must occur in it verbatim,
// supersede/close must name an active entry.
// CO Web 0.129.0 (SPEC §3.6b, folder of a chat → Events): section "agreement" - who promised what, by when. No schema
// change: "who" leads the text ("<who>: <what>") and a valid "due" date ends it as " [due YYYY-MM-DD]", the one form
// CO Web reads (chatFolder.ts agreementOf).
declare(strict_types=1);

const CO_PROTOCOL_DUE_RE = '/\s*\[due \d{4}-\d{2}-\d{2}\]/u';

// The stored text of an agreement: the marker of the model's text is dropped, "who" put in front unless the text starts
// with it, a real date as " [due …]" at the end; null when nothing is left.
function co_protocol_agreement_text(string $text, mixed $who, mixed $due): ?string
{
    $text = trim(preg_replace(CO_PROTOCOL_DUE_RE, '', $text) ?? '');
    if ($text === '') {
        return null;
    }
    if (is_string($who)) {
        $who = trim(preg_replace('/[\[\]:\r\n]+/u', ' ', $who) ?? '');
        $who = trim(mb_substr($who, 0, 60, 'UTF-8'));
        // always "<who>: …" (CO Web reads the who before ": "), unless the text already starts so
        if ($who !== '' && !str_starts_with(mb_strtolower($text, 'UTF-8'), mb_strtolower($who, 'UTF-8') . ':')) {
            $text = $who . ': ' . $text;
        }
    }
    $text = trim(mb_substr($text, 0, 580, 'UTF-8'));
    if (is_string($due) && preg_match('/^(\d{4})-(\d{2})-(\d{2})$/', trim($due), $m) === 1
        && checkdate((int)$m[2], (int)$m[3], (int)$m[1])) {
        $text .= ' [due ' . trim($due) . ']';
    }
    return $text;
}

function co_protocol_norm(string $s): string
{
    $s = str_replace(['*', '`', '_', '>', '#'], '', $s);
    $s = preg_replace('/\s+/u', ' ', $s) ?? '';
    return trim(mb_strtolower($s, 'UTF-8'));
}

// The chat's cursor (chats.protocol_seq = $batchEnd) moves in the same transaction, so a retried batch cannot add
// its entries twice.
// $extra (schema 9) runs inside the same transaction before the cursor moves: memory candidates of the batch.
// CO Web 0.134.0 (msgedit-lib.php): with $from (the cursor the batch was read after) a batch is dropped as stale when the
// cursor moved meanwhile (a rewrite took it back) or a message of the batch ($batch keys = seqs) was hidden; nothing is written, the cursor
// stays, the next run reads again. The chat row is locked first, the same order as msgedit_hide.
function co_protocol_apply(mysqli $db, string $chatId, array $batch, array $ops, string $model, int $batchEnd, ?callable $extra = null, ?int $from = null): array
{
    $allowedSections = ['decision', 'constraint', 'fact', 'question', 'artifact', 'answer', 'agreement'];
    $quoteRequired   = ['decision', 'constraint', 'fact', 'agreement'];
    $applied  = 0;
    $rejected = 0;
    $total    = count($ops);

    $toInt = static function (mixed $v): ?int {
        if (is_int($v)) {
            return $v;
        }
        if (is_string($v) && preg_match('/^-?\d{1,18}$/', trim($v)) === 1) {
            return (int)trim($v);
        }
        return null;
    };

    $model = mb_substr($model, 0, 60, 'UTF-8');

    $db->begin_transaction();
    try {
        if ($from !== null) {
            $st = $db->prepare('SELECT COALESCE(protocol_seq, 0) FROM chats WHERE chat_id = ? FOR UPDATE');
            $st->execute([$chatId]);
            $cur = $st->get_result()->fetch_row();
            $st->close();
            // the messages read (keys of $batch), not the whole range: older hidden ones in between were never read
            $seqs = array_map('intval', array_keys($batch));
            $hidden = false;
            if ($seqs) {
                $st = $db->prepare('SELECT 1 FROM events WHERE chat_id = ? AND seq IN (' . implode(',', array_fill(0, count($seqs), '?'))
                    . ") AND JSON_EXTRACT(data, '$.deleted') IS NOT NULL LIMIT 1");
                $st->execute([$chatId, ...$seqs]);
                $hidden = $st->get_result()->fetch_row() !== null;
                $st->close();
            }
            if ($cur === null || (int)$cur[0] !== $from || $hidden) {
                $db->rollback();
                return ['applied' => 0, 'rejected' => 0, 'total' => $total, 'stale' => true];
            }
        }
        $active = [];
        $st = $db->prepare("SELECT entry_no FROM protocol WHERE chat_id=? AND status='active' FOR UPDATE");
        $st->execute([$chatId]);
        $res = $st->get_result();
        while ($row = $res->fetch_row()) {
            $active[(int)$row[0]] = true;
        }
        $st->close();

        $st = $db->prepare('SELECT COALESCE(MAX(entry_no),0) FROM protocol WHERE chat_id=? FOR UPDATE');
        $st->execute([$chatId]);
        $row = $st->get_result()->fetch_row();
        $st->close();
        $nextNo = (int)($row[0] ?? 0) + 1;

        $insSt = $db->prepare(
            "INSERT INTO protocol (chat_id, entry_no, section, text, quote, msg_seq, status, author, model, created_utc, updated_utc)
             VALUES (?, ?, ?, ?, ?, ?, 'active', 'secretary', ?, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3))"
        );
        $supSt = $db->prepare(
            "UPDATE protocol SET status='superseded', replaced_by=?, closed_seq=?, updated_utc=UTC_TIMESTAMP(3)
             WHERE chat_id=? AND entry_no=?"
        );
        $clsSt = $db->prepare(
            "UPDATE protocol SET status='closed', closed_seq=?, updated_utc=UTC_TIMESTAMP(3)
             WHERE chat_id=? AND entry_no=?"
        );

        foreach ($ops as $op) {
            if (!is_array($op)) {
                $rejected++;
                continue;
            }

            $type = $op['op'] ?? null;
            if (!is_string($type) || !in_array($type, ['add', 'supersede', 'close'], true)) {
                $rejected++;
                continue;
            }

            $msg = $toInt($op['msg'] ?? null);
            if ($msg === null || !array_key_exists($msg, $batch)
                || !is_array($batch[$msg]) || !is_string($batch[$msg]['text'] ?? null)) {
                $rejected++;
                continue;
            }
            $msgText = $batch[$msg]['text'];

            $no = null;
            if ($type === 'supersede' || $type === 'close') {
                $no = $toInt($op['no'] ?? null);
                if ($no === null || !isset($active[$no])) {
                    $rejected++;
                    continue;
                }
            }

            if ($type === 'close') {
                $clsSt->execute([$msg, $chatId, $no]);
                unset($active[$no]);
                $applied++;
                continue;
            }

            // add / supersede
            $section = $op['section'] ?? null;
            if (!is_string($section) || !in_array($section, $allowedSections, true)) {
                $rejected++;
                continue;
            }

            $text = $op['text'] ?? null;
            if (!is_string($text)) {
                $rejected++;
                continue;
            }
            $text = preg_replace('/\R+/u', ' ', trim($text));
            if ($text === null) {
                $rejected++;
                continue;
            }
            $text = trim(mb_substr($text, 0, 600, 'UTF-8'));
            if ($section === 'agreement') {
                $text = co_protocol_agreement_text($text, $op['who'] ?? null, $op['due'] ?? null) ?? '';
            }
            if ($text === '') {
                $rejected++;
                continue;
            }

            $quote = $op['quote'] ?? null;
            if (is_string($quote)) {
                $quote = trim(mb_substr(trim($quote), 0, 300, 'UTF-8'));
                if ($quote === '') {
                    $quote = null;
                }
            } else {
                $quote = null;
            }

            $quoteFound = false;
            if ($quote !== null) {
                $nq = co_protocol_norm($quote);
                $quoteFound = $nq !== '' && str_contains(co_protocol_norm($msgText), $nq);
            }

            if (in_array($section, $quoteRequired, true)) {
                if (!$quoteFound) {
                    $rejected++;
                    continue;
                }
            } elseif (!$quoteFound) {
                $quote = null;
            }

            $newNo = $nextNo;
            $insSt->execute([$chatId, $newNo, $section, $text, $quote, $msg, $model]);
            $nextNo++;
            $active[$newNo] = true;

            if ($type === 'supersede') {
                $supSt->execute([$newNo, $msg, $chatId, $no]);
                unset($active[$no]);
            }

            $applied++;
        }

        $insSt->close();
        $supSt->close();
        $clsSt->close();

        if ($extra !== null) {
            $extra($db);
        }

        $st = $db->prepare('UPDATE chats SET protocol_seq = ?, protocol_utc = UTC_TIMESTAMP(3), protocol_model = ?, protocol_error = ? WHERE chat_id = ?');
        $st->execute([$batchEnd, $model, $rejected > 0 ? 'rejected ' . $rejected . ' of ' . $total : null, $chatId]);

        $db->commit();
    } catch (Throwable $e) {
        $db->rollback();
        throw $e;
    }

    return ['applied' => $applied, 'rejected' => $rejected, 'total' => $total];
}
