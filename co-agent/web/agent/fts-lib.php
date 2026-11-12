<?php
// SPDX-License-Identifier: AGPL-3.0-or-later
// fts-lib.php: word stems for the full-text search (2026-11-07).
// MariaDB FULLTEXT knows only whole words: "капча" did not find "капчу", "tests" did not find "testing". The index stays
// as it is; the query is rewritten instead: every word goes in as itself plus its stem as a prefix term in BOOLEAN MODE
// ("капчу" -> "капчу капч*"). The exact form still scores higher (it matches both terms). Stemmers: Snowball Russian
// (also catches the common Ukrainian endings) and a light English one; other languages and ids stay exact words.
declare(strict_types=1);

// Stems shorter than this are not used as prefixes ("дом*" would find "домен").
const CO_FTS_MIN_STEM = 4;
const CO_FTS_MAX_WORDS = 30;
// Length damping of the score (2026-11-08): InnoDB scores a row by term count x IDF^2 without length normalisation, so a long
// tool dump or book chapter beat the short message about the topic. Score x K / (K + characters): rows up to ~K characters
// keep about their score, a 30k-character row gets a tenth. K from a sweep over 22 golden questions (prod, all chats of
// the owner): MRR 0.36 -> 0.66, right chat in the top 5 for 12 -> 21 of 22; K 1500..8000 all gave about the same.
const CO_FTS_LEN_K = 3000;
// Function words of 3+ letters that the index keeps (its stop list is English): left out of the query unless nothing else
// is left ("резидент для мака" matched every message with "для").
const CO_FTS_STOP = ['для', 'что', 'как', 'это', 'эта', 'этот', 'эти', 'или', 'при', 'без', 'над', 'под', 'про', 'все', 'всё',
    'так', 'уже', 'ещё', 'еще', 'его', 'она', 'они', 'оно', 'мне', 'меня', 'нас', 'вас', 'там', 'тут', 'где', 'кто', 'чем',
    'чтобы', 'когда', 'если', 'только', 'тоже', 'также', 'через', 'после', 'перед', 'между', 'который', 'которая', 'которые',
    'есть', 'был', 'была', 'было', 'были', 'будет', 'нет', 'вот', 'даже', 'ли', 'що', 'як', 'це', 'або', 'від', 'але', 'вже',
    'and', 'not', 'but', 'are', 'was', 'has', 'have', 'into', 'than', 'then', 'them', 'they', 'your', 'our', 'its', 'can'];

/** Stem of one lower-case word: Russian for Cyrillic, light English for Latin, the word itself otherwise. */
function co_stem(string $w): string
{
    $w = mb_strtolower($w, 'UTF-8');
    if (preg_match('/^[\p{Cyrillic}]+$/u', $w) === 1) {
        return co_stem_ru($w);
    }
    if (preg_match('/^[a-z]+$/', $w) === 1) {
        return co_stem_en($w);
    }
    return $w;
}

/** Snowball Russian stemmer (snowballstem.org/algorithms/russian), regex form. */
function co_stem_ru(string $w): string
{
    static $re = null;
    $re ??= [
        'gerund' => '/((ив|ивши|ившись|ыв|ывши|ывшись)|((?<=[ая])(в|вши|вшись)))$/u',
        'reflexive' => '/(ся|сь)$/u',
        'adjective' => '/(ее|ие|ые|ое|ими|ыми|ей|ий|ый|ой|ем|им|ым|ом|его|ого|ему|ому|их|ых|ую|юю|ая|яя|ою|ею)$/u',
        'participle' => '/((ивш|ывш|ующ)|((?<=[ая])(ем|нн|вш|ющ|щ)))$/u',
        'verb' => '/((ила|ыла|ена|ейте|уйте|ите|или|ыли|ей|уй|ил|ыл|им|ым|ен|ило|ыло|ено|ят|ует|уют|ит|ыт|ены|ить|ыть|ишь|ую|ю)'
            . '|((?<=[ая])(ла|на|ете|йте|ли|й|л|ем|н|ло|но|ет|ют|ны|ть|ешь|нно)))$/u',
        'noun' => '/(а|ев|ов|ие|ье|е|иями|ями|ами|еи|ии|и|ией|ей|ой|ий|й|иям|ям|ием|ем|ам|ом|о|у|ах|иях|ях|ы|ь|ию|ью|ю|ия|ья|я)$/u',
    ];
    $w = str_replace('ё', 'е', $w);
    // RV: the part after the first vowel; all endings are removed inside it
    if (preg_match('/^(.*?[аеиоуыэюя])(.*)$/u', $w, $m) !== 1) {
        return $w;
    }
    [$head, $rv] = [$m[1], $m[2]];
    $cut = static function (string $pattern, string $to = '') use (&$rv): bool {
        $new = (string)preg_replace($pattern, $to, $rv, 1);
        if ($new === $rv) {
            return false;
        }
        $rv = $new;
        return true;
    };
    if (!$cut($re['gerund'])) {
        $cut($re['reflexive']);
        if ($cut($re['adjective'])) {
            $cut($re['participle']);
        } elseif (!$cut($re['verb'])) {
            $cut($re['noun']);
        }
    }
    $cut('/и$/u');
    // derivational ость/ост only inside R2
    if (preg_match('/ость?$/u', $rv, $d) === 1) {
        $word = $head . $rv;
        $r2 = co_stem_ru_r2($word);
        if (mb_strlen($word, 'UTF-8') - mb_strlen($d[0], 'UTF-8') >= $r2) {
            $rv = mb_substr($rv, 0, mb_strlen($rv, 'UTF-8') - mb_strlen($d[0], 'UTF-8'), 'UTF-8');
        }
    }
    if (!$cut('/ь$/u')) {
        $cut('/ейше?$/u');
        $cut('/нн$/u', 'н');
    }
    return $head . $rv;
}

/** Start of R2 (in letters) of a Russian word: R1 = after the first non-vowel that follows a vowel, R2 = the same in R1. */
function co_stem_ru_r2(string $w): int
{
    $ch = mb_str_split($w, 1, 'UTF-8');
    $n = count($ch);
    $region = static function (int $start) use ($ch, $n): int {
        for ($i = $start + 1; $i < $n; $i++) {
            if (!str_contains('аеиоуыэюя', $ch[$i]) && str_contains('аеиоуыэюя', $ch[$i - 1])) {
                return $i + 1;
            }
        }
        return $n;
    };
    return $region($region(0));
}

/** Light English stemmer: plural, -ing, -ed, -ly, then a final e/y/i and a doubled consonant go. */
function co_stem_en(string $w): string
{
    if (strlen($w) <= 4) {
        return $w;
    }
    $w = (string)preg_replace("/'s$/", '', $w);
    // [suffix, replacement, letters that must stay]
    $rules = [['ingly', '', 3], ['edly', '', 3], ['ings', '', 3], ['ing', '', 3], ['ies', 'i', 3], ['ied', 'i', 3], ['ed', '', 3], ['ly', '', 4]];
    $done = false;
    foreach ($rules as [$suf, $to, $keep]) {
        if (str_ends_with($w, $suf) && strlen($w) - strlen($suf) >= $keep) {
            $w = substr($w, 0, -strlen($suf)) . $to;
            $done = true;
            break;
        }
    }
    if (!$done && str_ends_with($w, 's') && strlen($w) >= 5) {
        if (preg_match('/(s|x|z|ch|sh)es$/', $w) === 1) {
            $w = substr($w, 0, -2);
        } elseif (preg_match('/[^siu]s$/', $w) === 1) {
            $w = substr($w, 0, -1);
        }
    } elseif ($done && preg_match('/([bdfgkmnprt])\1$/', $w) === 1) {
        $w = substr($w, 0, -1);
    }
    if (strlen($w) > 4) {
        $w = (string)preg_replace('/[eyi]$/', '', $w);
    }
    return $w;
}

/** Words of a query as the index sees them: letters, digits, underscore; lower case, unique, at most 30. */
function co_fts_words(string $q): array
{
    preg_match_all('/[\p{L}\p{N}_]+/u', mb_strtolower($q, 'UTF-8'), $m);
    return array_slice(array_values(array_unique($m[0])), 0, CO_FTS_MAX_WORDS);
}

/**
 * BOOLEAN MODE query for MATCH ... AGAINST: each word as itself plus "stem*" when the stem has 4+ letters; no required
 * terms, so any word matches (as NATURAL LANGUAGE MODE did) and rows with more and rarer words rank higher. Words under
 * 3 characters are below the index token size and are left out, function words too (CO_FTS_STOP) unless the query has
 * nothing else. '' when nothing is left: callers return no rows.
 */
function co_fts_query(string $q): string
{
    static $stop = null;
    $stop ??= array_fill_keys(CO_FTS_STOP, true);
    $words = array_values(array_filter(co_fts_words($q), static fn($w) => mb_strlen($w, 'UTF-8') >= 3));
    $content = array_values(array_filter($words, static fn($w) => !isset($stop[str_replace('ё', 'е', $w)])));
    $terms = [];
    foreach ($content ?: $words as $w) {
        $terms[$w] = true;
        $s = co_fts_prefix($w);
        if ($s !== '') {
            $terms[$s . '*'] = true;
        }
    }
    return implode(' ', array_keys($terms));
}

/**
 * Prefix term of a word: its stem, or (Cyrillic, stem under 4 letters, word of 5+) its first 4 letters, so that "копии" and
 * "копия" (stem "коп") still meet as "копи*". '' for short words, digits and ids.
 */
function co_fts_prefix(string $w): string
{
    if (preg_match('/^\p{L}+$/u', $w) !== 1) {
        return '';
    }
    $s = co_stem($w);
    $n = mb_strlen($s, 'UTF-8');
    if ($n < CO_FTS_MIN_STEM && mb_strlen($w, 'UTF-8') >= CO_FTS_MIN_STEM + 1 && preg_match('/^\p{Cyrillic}+$/u', $w) === 1) {
        $s = mb_substr(mb_strtolower($w, 'UTF-8'), 0, CO_FTS_MIN_STEM, 'UTF-8');
        $n = CO_FTS_MIN_STEM;
    }
    return $n >= CO_FTS_MIN_STEM ? $s : '';
}

/** SQL score of a full-text match damped by the length of the matched text (see CO_FTS_LEN_K); same units as MATCH. */
function co_fts_score_sql(string $match, string $text): string
{
    return '(' . $match . ') * ' . CO_FTS_LEN_K . ' / (' . CO_FTS_LEN_K . ' + CHAR_LENGTH(' . $text . '))';
}

/** Strings to look for when cutting a snippet around a hit: the stems first (they cover every form), then the words. */
function co_fts_needles(string $q): array
{
    $out = [];
    foreach (co_fts_words($q) as $w) {
        $s = co_fts_prefix($w);
        if ($s !== '') {
            $out[$s] = true;
        }
        $out[$w] = true;
    }
    return array_map('strval', array_keys($out)); // numeric keys ("2026") come back as int
}

// ~200-char snippet around the first occurrence of any query word.
function db_snippet(string $text, string $q, int $len = 200): string
{
    $text = trim((string)preg_replace('/\s+/u', ' ', $text));
    $total = mb_strlen($text);
    $words = co_fts_needles($q);

    $pos = null;
    foreach ($words as $w) {
        $p = mb_stripos($text, $w);
        if ($p !== false && ($pos === null || $p < $pos)) {
            $pos = $p;
        }
    }

    $start = 0;
    if ($pos !== null) {
        $start = max(0, $pos - intdiv($len, 3));
        if ($start + $len > $total) {
            $start = max(0, $total - $len);
        }
    }

    $snip = mb_substr($text, $start, $len);
    if ($start > 0) {
        $snip = '…' . ltrim($snip);
    }
    if ($start + $len < $total) {
        $snip = rtrim($snip) . '…';
    }
    return $snip;
}
