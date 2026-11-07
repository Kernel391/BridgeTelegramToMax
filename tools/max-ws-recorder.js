// Запись WebSocket-кадров web.max.ru с маскированием содержимого (v2: понимает бинарные кадры).
//
// Как пользоваться: откройте web.max.ru (лучше на экране QR-входа, до авторизации),
// откройте DevTools (F12) -> Console, вставьте этот файл целиком и нажмите Enter.
// Дальше входите в аккаунт как обычно. Чтобы забрать результат, выполните в консоли:
//     copy(__maxRec.dump())          // скопирует всё в буфер обмена
//     __maxRec.dump(10)              // только кадры начиная с 10-го
//
// Формат кадров, который видит веб-клиент:
//   текстовый кадр  -> JSON {ver, cmd, seq, opcode, payload}
//   бинарный кадр   -> 10 байт заголовка (ver u8, cmd u8, seq u16, opcode u16, flags u8 + длина u24,
//                      все числа big-endian) и далее payload в формате MessagePack.
//                      flags != 0 означает сжатие (LZ4/Zstd), такой payload не разбирается.
//
// Что скрывается: все строки заменяются на <str:длина> (кроме безопасных полей вроде type или
// appVersion), бинарные поля на <bin:длина>, большие числа (ID, время) на стабильные псевдонимы
// вида #3/13d (номер/число цифр), поэтому видно, где один и тот же ID встречается в разных
// местах. Тексты сообщений, токены, телефоны и имена в дамп не попадают.
//
// Ограничение: записываются только кадры, отправленные/полученные ПОСЛЕ вставки скрипта.
// Рукопожатие и вход по токену при загрузке страницы лучше смотреть во вкладке
// DevTools -> Network -> WS (откройте её ДО загрузки страницы).
(() => {
  if (window.__maxRec) return 'уже установлена';

  const KEEP = new Set([
    'type', '_type', 'status', 'deviceType', 'appVersion', 'locale', 'deviceLocale',
    'osVersion', 'deviceName', 'authTokenType', 'tokenType', 'timezone', 'screen', 'error',
  ]);
  const t0 = performance.now();
  const frames = [];
  const ids = new Map();

  class Bin { constructor(n) { this.n = n; } }

  const alias = (n) => {
    const k = String(n);
    if (!ids.has(k)) ids.set(k, ids.size + 1);
    return `#${ids.get(k)}/${k.replace('-', '').length}d`;
  };

  const mask = (v, key) => {
    if (v === null || typeof v === 'boolean') return v;
    if (v instanceof Bin) return `<bin:${v.n}>`;
    if (typeof v === 'bigint') {
      return (v > -1000n && v < 1000n) ? Number(v) : alias(v.toString());
    }
    if (typeof v === 'number') {
      if (Number.isInteger(v) && Math.abs(v) < 1000) return v;
      return alias(Math.trunc(v));
    }
    if (typeof v === 'string') return KEEP.has(key) ? v : `<str:${v.length}>`;
    if (Array.isArray(v)) {
      const out = v.slice(0, 3).map((x) => mask(x, key));
      if (v.length > 3) out.push(`<+${v.length - 3} more>`);
      return out;
    }
    if (typeof v === 'object') {
      const o = {};
      for (const k of Object.keys(v)) {
        const safeKey = /^-?\d{4,}$/.test(k) ? alias(k) : k; // ключи-ID тоже скрываем
        o[safeKey] = mask(v[k], k);
      }
      return o;
    }
    return `<${typeof v}>`;
  };

  // Минимальный декодер MessagePack (достаточно для payload протокола).
  const decodeMsgpack = (u8) => {
    const dv = new DataView(u8.buffer, u8.byteOffset, u8.byteLength);
    const td = new TextDecoder();
    let p = 0;
    const str = (n) => { const s = td.decode(u8.subarray(p, p + n)); p += n; return s; };
    const bin = (n) => { p += n; return new Bin(n); };
    const arr = (n) => { const a = []; for (let i = 0; i < n; i++) a.push(read()); return a; };
    const map = (n) => { const o = {}; for (let i = 0; i < n; i++) { const k = read(); o[String(k)] = read(); } return o; };
    const u16 = () => { const v = dv.getUint16(p); p += 2; return v; };
    const u32 = () => { const v = dv.getUint32(p); p += 4; return v; };
    const read = () => {
      const b = u8[p++];
      if (b <= 0x7f) return b;
      if (b >= 0xe0) return b - 256;
      if ((b & 0xe0) === 0xa0) return str(b & 0x1f);
      if ((b & 0xf0) === 0x80) return map(b & 0x0f);
      if ((b & 0xf0) === 0x90) return arr(b & 0x0f);
      switch (b) {
        case 0xc0: return null;
        case 0xc2: return false;
        case 0xc3: return true;
        case 0xc4: return bin(u8[p++]);
        case 0xc5: return bin(u16());
        case 0xc6: return bin(u32());
        case 0xc7: { const n = u8[p++]; p += 1; return bin(n); }
        case 0xc8: { const n = u16(); p += 1; return bin(n); }
        case 0xc9: { const n = u32(); p += 1; return bin(n); }
        case 0xca: { const v = dv.getFloat32(p); p += 4; return v; }
        case 0xcb: { const v = dv.getFloat64(p); p += 8; return v; }
        case 0xcc: return u8[p++];
        case 0xcd: return u16();
        case 0xce: return u32();
        case 0xcf: { const v = dv.getBigUint64(p); p += 8; return v; }
        case 0xd0: { const v = dv.getInt8(p); p += 1; return v; }
        case 0xd1: { const v = dv.getInt16(p); p += 2; return v; }
        case 0xd2: { const v = dv.getInt32(p); p += 4; return v; }
        case 0xd3: { const v = dv.getBigInt64(p); p += 8; return v; }
        // fixext: байт типа + 1/2/4/8/16 байт данных
        case 0xd4: return bin(2);
        case 0xd5: return bin(3);
        case 0xd6: return bin(5);
        case 0xd7: return bin(9);
        case 0xd8: return bin(17);
        case 0xd9: return str(u8[p++]);
        case 0xda: return str(u16());
        case 0xdb: return str(u32());
        case 0xdc: return arr(u16());
        case 0xdd: return arr(u32());
        case 0xde: return map(u16());
        case 0xdf: return map(u32());
        default: throw new Error('msgpack: неизвестный тип 0x' + b.toString(16));
      }
    };
    const value = read();
    if (p !== u8.length) throw new Error(`msgpack: лишние байты (${u8.length - p})`);
    return value;
  };

  // Декодер LZ4 block (без заголовка кадра): последовательности «токен, литералы, смещение, повтор».
  const lz4Block = (src) => {
    const out = [];
    let i = 0;
    const len = () => { let n = 0; let b; do { b = src[i++]; n += b; } while (b === 255); return n; };
    while (i < src.length) {
      const token = src[i++];
      let lit = token >> 4;
      if (lit === 15) lit += len();
      for (let k = 0; k < lit; k++) out.push(src[i++]);
      if (i >= src.length) break; // последняя последовательность содержит только литералы
      const offset = src[i] | (src[i + 1] << 8);
      i += 2;
      let match = token & 15;
      if (match === 15) match += len();
      match += 4;
      let pos = out.length - offset;
      if (offset === 0 || pos < 0) throw new Error('lz4: неверное смещение');
      for (let k = 0; k < match; k++) out.push(out[pos++]);
    }
    return Uint8Array.from(out);
  };

  const decodeBinaryFrame = (u8) => {
    if (u8.length < 10) return `<binary:${u8.length}, короче заголовка>`;
    const dv = new DataView(u8.buffer, u8.byteOffset, u8.byteLength);
    const packed = dv.getUint32(6);
    const flags = packed >>> 24;
    const len = packed & 0xffffff;
    const head = { ver: u8[0], cmd: u8[1], seq: dv.getUint16(2), opcode: dv.getUint16(4), flags, len };
    if (10 + len !== u8.length) return { ...head, payload: `<длина в заголовке не сходится: всего ${u8.length} байт>` };
    if (len === 0) return { ...head, payload: null };
    const body = u8.subarray(10);
    if (flags === 0xff) return { ...head, payload: '<zstd, не разбирается>' };
    try {
      if (flags === 0) return { ...head, payload: mask(decodeMsgpack(body)) };
      // flags 1..254: LZ4. Если сразу после распаковки msgpack не сходится, пробуем пропустить
      // 4 байта (в некоторых реализациях перед данными стоит длина распакованного блока).
      try {
        return { ...head, lz4: true, payload: mask(decodeMsgpack(lz4Block(body))) };
      } catch {
        return { ...head, lz4: true, skip4: true, payload: mask(decodeMsgpack(lz4Block(body.subarray(4)))) };
      }
    } catch (e) {
      return { ...head, payload: `<ошибка разбора: ${e.message}>` };
    }
  };

  const toBytes = (data) => {
    if (data instanceof Blob) return data.arrayBuffer().then((b) => new Uint8Array(b));
    if (data instanceof ArrayBuffer) return Promise.resolve(new Uint8Array(data));
    if (ArrayBuffer.isView(data)) return Promise.resolve(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
    return Promise.reject(new Error('неизвестный тип данных'));
  };

  const record = (dir, sock, data) => {
    let ws = '';
    try { const u = new URL(sock.url); ws = u.host + u.pathname; } catch { /* ignore */ }
    // Запись добавляется сразу, чтобы сохранить порядок; бинарное содержимое дописывается позже.
    const entry = { i: frames.length, t: Math.round(performance.now() - t0), dir, ws, kind: '', frame: null };
    frames.push(entry);
    if (typeof data === 'string') {
      entry.kind = 'text';
      try {
        const f = JSON.parse(data);
        entry.frame = { ...f, payload: mask(f.payload) };
      } catch {
        entry.frame = `<non-json text:${data.length}>`;
      }
      return;
    }
    entry.kind = 'binary';
    toBytes(data).then((u8) => { entry.frame = decodeBinaryFrame(u8); })
      .catch((e) => { entry.frame = `<не удалось прочитать: ${e.message}>`; });
  };

  // Исходящие: send живёт в прототипе, поэтому перехват действует и на уже открытый сокет.
  let lastSocket = null;
  const origSend = WebSocket.prototype.send;
  WebSocket.prototype.send = function (data) {
    lastSocket = this; // запоминаем сокет, чтобы можно было вызвать reconnect()
    try { record('out', this, data); } catch { /* запись не должна ломать сайт */ }
    return origSend.call(this, data);
  };

  // Входящие: подменяем геттер MessageEvent.data, он вызывается, когда сайт читает сообщение.
  const desc = Object.getOwnPropertyDescriptor(MessageEvent.prototype, 'data');
  const seen = new WeakSet();
  Object.defineProperty(MessageEvent.prototype, 'data', {
    configurable: true,
    get() {
      const value = desc.get.call(this);
      try {
        if (this.target instanceof WebSocket && !seen.has(this)) {
          seen.add(this);
          record('in', this.target, value);
        }
      } catch { /* ignore */ }
      return value;
    },
  });

  window.__maxRec = {
    frames,
    dump: (from = 0) => JSON.stringify(frames.slice(from), null, 1),
    clear: () => { frames.length = 0; },
    // Закрывает текущий сокет: сайт переподключится, и мы запишем рукопожатие и вход по токену.
    reconnect: () => { if (lastSocket) { lastSocket.close(); return 'сокет закрыт'; } return 'сокет ещё не найден'; },
  };
  return 'установлена';
})();
