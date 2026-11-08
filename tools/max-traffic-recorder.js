// Запись трафика web.max.ru с маскированием содержимого: кадры WebSocket и HTTP-запросы, которые меняют данные
// (POST, PUT и так далее, например загрузка файла или фото). Версия 3.
//
// Как пользоваться: откройте web.max.ru (лучше на экране QR-входа, до авторизации),
// откройте DevTools (F12) -> Console, вставьте этот файл целиком и нажмите Enter.
// Дальше входите в аккаунт как обычно. Чтобы забрать результат, выполните в консоли:
//
//     copy(__maxTrafficRecorder.dump())          // копирует ВСЕ кадры в буфер обмена одной строкой, без переводов строк
//     copy(__maxTrafficRecorder.dump(10))        // то же, но только кадры начиная с десятого (счёт с нуля)
//     __maxTrafficRecorder.show()                // печатает кадры в консоль как раскрывающиеся объекты, чтобы читать глазами
//     __maxTrafficRecorder.show(10)              // то же, начиная с десятого кадра
//     __maxTrafficRecorder.clear()               // стирает записанное
//     __maxTrafficRecorder.reconnect()           // закрывает сокет: сайт переподключится, и запишется рукопожатие и вход
//
// Записи идут в одном списке в порядке прихода, у каждой есть время elapsedMs от вставки скрипта. Поэтому видно, что
// загрузке файла предшествовало (запрос адреса по WebSocket), что последовало дальше (уведомление «файл готов»).
//
// Формат кадров, который видит веб-клиент:
//   текстовый кадр  -> JSON {ver, cmd, seq, opcode, payload}. Так называются поля в самом протоколе (старая версия
//                      протокола), поэтому здесь они сохранены как есть.
//   бинарный кадр   -> 10 байт заголовка и далее тело в формате MessagePack. Заголовок (все числа big-endian):
//                        версия протокола            1 байт
//                        команда                     1 байт (0 запрос или уведомление, 1 ответ, 3 ошибка)
//                        номер запроса (requestId)   2 байта
//                        код операции (opcode)       2 байта
//                        флаги сжатия                1 байт (0 без сжатия, 1..254 LZ4, 255 Zstd)
//                        длина тела                  3 байта
//
// В записи бинарного кадра заголовок разложен на поля version, command, requestId, opcode, compressionFlags,
// bodyLength, а содержимое лежит в поле body. Если тело было сжато LZ4, добавляется lz4: true.
//
// HTTP-запрос записывается одной записью с frameKind "http": поля method, url, requestHeaders, requestBody и,
// когда сервер ответил, response (status, headers, body). Запрос пишется сразу при отправке, ответ дописывается позже.
//   url             хост и путь как есть, значения параметров скрыты (числа превращаются в псевдонимы, так что
//                   id файла из адреса можно сопоставить с fileId из кадра WebSocket)
//   requestHeaders  заголовки, которые выставил сам сайт. Content-Type, Content-Disposition, Content-Range и ещё
//                   несколько безопасных показываются как есть, остальные (Cookie, Authorization...) скрыты
//   requestBody     только описание: вид, размер, тип. Содержимое файла в дамп не попадает
// GET-запросы (картинки, скрипты, аналитика) по умолчанию не пишутся, это настраивается константой
// RECORD_ONLY_HTTP_REQUESTS_THAT_CHANGE_DATA ниже.
//
// Чего в HTTP-записях не будет: заголовков, которые браузер добавляет сам и которых JavaScript не видит
// (Origin, Cookie, User-Agent, Content-Length). Они нужны редко; если нужны, смотрите вкладку DevTools -> Network.
// Не перехватываются запросы из Web Worker и через navigator.sendBeacon.
//
// Что скрывается: все строки заменяются на <str:длина> (кроме безопасных полей вроде type или appVersion),
// бинарные значения на <bin:длина>, большие числа (ID, время) на стабильные псевдонимы вида #3/13d
// (порядковый номер числа / сколько в нём цифр), поэтому видно, где один и тот же ID встречается в разных местах.
// Тексты сообщений, токены, телефоны и имена в дамп не попадают.
//
// Ограничение: записываются только кадры, отправленные и полученные ПОСЛЕ вставки скрипта. Рукопожатие и вход по
// токену при загрузке страницы лучше смотреть во вкладке DevTools -> Network -> WS (откройте её ДО загрузки страницы).
(() => {
  if (window.__maxTrafficRecorder) return 'уже установлена';

  // Строки в этих полях не скрываются: в них нет личных данных, а по ним удобно понимать, что происходит.
  const FIELD_NAMES_WITH_SAFE_STRINGS = new Set([
    'type', '_type', 'status', 'deviceType', 'appVersion', 'locale', 'deviceLocale',
    'osVersion', 'deviceName', 'authTokenType', 'tokenType', 'timezone', 'screen', 'error',
  ]);

  // Сколько элементов массива показывать в записи, остальные заменяются пометкой «ещё N».
  const MAX_ARRAY_ITEMS_SHOWN = 3;

  const recordingStartedAtMs = performance.now();
  const recordedFrames = [];

  // Число (ID, время) -> порядковый номер псевдонима. Одно и то же число всегда получает один и тот же псевдоним.
  const aliasNumbers = new Map();

  // Бинарное значение из MessagePack: само содержимое нам не нужно, запоминаем только размер.
  class BinaryValue {
    constructor(byteLength) {
      this.byteLength = byteLength;
    }
  }

  // Заменяет число на псевдоним вида #3/13d: номер псевдонима и количество цифр в числе.
  const aliasForNumber = (number) => {
    const numberText = String(number);
    if (!aliasNumbers.has(numberText)) aliasNumbers.set(numberText, aliasNumbers.size + 1);
    const digitCount = numberText.replace('-', '').length;
    return `#${aliasNumbers.get(numberText)}/${digitCount}d`;
  };

  // Возвращает копию значения, из которой убрано всё личное: строки, бинарные данные и большие числа.
  // fieldName нужен, чтобы оставить без изменений безопасные поля (см. FIELD_NAMES_WITH_SAFE_STRINGS).
  const maskValue = (value, fieldName) => {
    if (value === null || typeof value === 'boolean') return value;
    if (value instanceof BinaryValue) return `<bin:${value.byteLength}>`;
    if (typeof value === 'bigint') {
      const isSmall = value > -1000n && value < 1000n;
      return isSmall ? Number(value) : aliasForNumber(value.toString());
    }
    if (typeof value === 'number') {
      const isSmallInteger = Number.isInteger(value) && Math.abs(value) < 1000;
      return isSmallInteger ? value : aliasForNumber(Math.trunc(value));
    }
    if (typeof value === 'string') {
      return FIELD_NAMES_WITH_SAFE_STRINGS.has(fieldName) ? value : `<str:${value.length}>`;
    }
    if (Array.isArray(value)) {
      const shownItems = value.slice(0, MAX_ARRAY_ITEMS_SHOWN).map((item) => maskValue(item, fieldName));
      if (value.length > MAX_ARRAY_ITEMS_SHOWN) shownItems.push(`<ещё ${value.length - MAX_ARRAY_ITEMS_SHOWN}>`);
      return shownItems;
    }
    if (typeof value === 'object') {
      const maskedObject = {};
      for (const key of Object.keys(value)) {
        // Ключи, которые сами являются ID (4 цифры и больше), тоже скрываем.
        const maskedKey = /^-?\d{4,}$/.test(key) ? aliasForNumber(key) : key;
        maskedObject[maskedKey] = maskValue(value[key], key);
      }
      return maskedObject;
    }
    return `<${typeof value}>`;
  };

  // Минимальный декодер MessagePack: достаточно для тел кадров этого протокола.
  const decodeMessagePack = (bytes) => {
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const textDecoder = new TextDecoder();
    let position = 0; // откуда читаем следующий байт

    const readString = (byteLength) => {
      const text = textDecoder.decode(bytes.subarray(position, position + byteLength));
      position += byteLength;
      return text;
    };
    const readBinary = (byteLength) => {
      position += byteLength;
      return new BinaryValue(byteLength);
    };
    // Расширение MessagePack (ext): байт типа, затем данные. MAX присылает ID (чатов, сообщений, файлов) как ext
    // типа 1, внутри которого обычное целое число. Такое число возвращаем числом, чтобы ему достался псевдоним
    // и один и тот же ID был узнаваем в разных кадрах и в адресах HTTP-запросов. Остальные расширения показываем
    // бинарным значением (только размер).
    const readExtension = (dataLength) => {
      const extensionType = bytes[position++];
      const data = bytes.subarray(position, position + dataLength);
      position += dataLength;
      if (extensionType === 1) {
        try { return decodeMessagePack(data); } catch { /* внутри не число: покажем как бинарное значение */ }
      }
      return new BinaryValue(dataLength);
    };
    const readArray = (itemCount) => {
      const items = [];
      for (let index = 0; index < itemCount; index++) items.push(readValue());
      return items;
    };
    const readMap = (pairCount) => {
      const map = {};
      for (let index = 0; index < pairCount; index++) {
        const key = readValue();
        map[String(key)] = readValue();
      }
      return map;
    };
    const readUint16 = () => {
      const number = view.getUint16(position);
      position += 2;
      return number;
    };
    const readUint32 = () => {
      const number = view.getUint32(position);
      position += 4;
      return number;
    };

    // Читает одно значение: первый байт (typeByte) говорит, что это за значение и как оно закодировано.
    const readValue = () => {
      const typeByte = bytes[position++];
      if (typeByte <= 0x7f) return typeByte;                              // маленькое положительное число
      if (typeByte >= 0xe0) return typeByte - 256;                        // маленькое отрицательное число
      if ((typeByte & 0xe0) === 0xa0) return readString(typeByte & 0x1f); // короткая строка
      if ((typeByte & 0xf0) === 0x80) return readMap(typeByte & 0x0f);    // короткая карта
      if ((typeByte & 0xf0) === 0x90) return readArray(typeByte & 0x0f);  // короткий массив
      switch (typeByte) {
        case 0xc0: return null;
        case 0xc2: return false;
        case 0xc3: return true;
        case 0xc4: return readBinary(bytes[position++]);
        case 0xc5: return readBinary(readUint16());
        case 0xc6: return readBinary(readUint32());
        // ext: длина данных, затем байт типа расширения и сами данные
        case 0xc7: return readExtension(bytes[position++]);
        case 0xc8: return readExtension(readUint16());
        case 0xc9: return readExtension(readUint32());
        case 0xca: { const number = view.getFloat32(position); position += 4; return number; }
        case 0xcb: { const number = view.getFloat64(position); position += 8; return number; }
        case 0xcc: return bytes[position++];
        case 0xcd: return readUint16();
        case 0xce: return readUint32();
        case 0xcf: { const number = view.getBigUint64(position); position += 8; return number; }
        case 0xd0: { const number = view.getInt8(position); position += 1; return number; }
        case 0xd1: { const number = view.getInt16(position); position += 2; return number; }
        case 0xd2: { const number = view.getInt32(position); position += 4; return number; }
        case 0xd3: { const number = view.getBigInt64(position); position += 8; return number; }
        // fixext: длина данных зашита в тип (1, 2, 4, 8 или 16 байт), затем байт типа расширения и данные
        case 0xd4: return readExtension(1);
        case 0xd5: return readExtension(2);
        case 0xd6: return readExtension(4);
        case 0xd7: return readExtension(8);
        case 0xd8: return readExtension(16);
        case 0xd9: return readString(bytes[position++]);
        case 0xda: return readString(readUint16());
        case 0xdb: return readString(readUint32());
        case 0xdc: return readArray(readUint16());
        case 0xdd: return readArray(readUint32());
        case 0xde: return readMap(readUint16());
        case 0xdf: return readMap(readUint32());
        default: throw new Error('msgpack: неизвестный тип 0x' + typeByte.toString(16));
      }
    };

    const decodedValue = readValue();
    if (position !== bytes.length) throw new Error(`msgpack: лишние байты (${bytes.length - position})`);
    return decodedValue;
  };

  // Декодер LZ4 block (без заголовка кадра). Данные состоят из последовательностей:
  // «токен, литералы (байты как есть), смещение назад, повтор уже распакованного куска».
  const decodeLz4Block = (compressed) => {
    const decompressed = [];
    let position = 0;

    // Длина может не поместиться в 4 бита токена: тогда к ней прибавляются следующие байты, пока они равны 255.
    const readExtraLength = () => {
      let extraLength = 0;
      let nextByte;
      do {
        nextByte = compressed[position++];
        extraLength += nextByte;
      } while (nextByte === 255);
      return extraLength;
    };

    while (position < compressed.length) {
      const token = compressed[position++];

      let literalLength = token >> 4; // старшие 4 бита токена: сколько литералов идёт следом
      if (literalLength === 15) literalLength += readExtraLength();
      for (let index = 0; index < literalLength; index++) decompressed.push(compressed[position++]);

      // Последняя последовательность содержит только литералы, повтора в ней нет.
      if (position >= compressed.length) break;

      const matchOffset = compressed[position] | (compressed[position + 1] << 8); // на сколько байт назад смотреть
      position += 2;

      let matchLength = token & 15; // младшие 4 бита токена: длина повтора минус 4
      if (matchLength === 15) matchLength += readExtraLength();
      matchLength += 4;

      let copyFrom = decompressed.length - matchOffset;
      if (matchOffset === 0 || copyFrom < 0) throw new Error('lz4: неверное смещение');
      for (let index = 0; index < matchLength; index++) decompressed.push(decompressed[copyFrom++]);
    }

    return Uint8Array.from(decompressed);
  };

  // Разбирает бинарный кадр: заголовок раскладывает по полям, тело декодирует (при необходимости сначала распаковывает).
  const decodeBinaryFrame = (frameBytes) => {
    if (frameBytes.length < 10) return `<бинарный кадр ${frameBytes.length} байт, короче заголовка>`;

    const view = new DataView(frameBytes.buffer, frameBytes.byteOffset, frameBytes.byteLength);
    const compressionFlagsAndBodyLength = view.getUint32(6);
    const compressionFlags = compressionFlagsAndBodyLength >>> 24;
    const bodyLength = compressionFlagsAndBodyLength & 0xffffff;
    const header = {
      version: frameBytes[0],
      command: frameBytes[1],
      requestId: view.getUint16(2),
      opcode: view.getUint16(4),
      compressionFlags,
      bodyLength,
    };

    if (10 + bodyLength !== frameBytes.length) {
      return { ...header, body: `<длина тела в заголовке не сходится: всего ${frameBytes.length} байт>` };
    }
    if (bodyLength === 0) return { ...header, body: null };

    const bodyBytes = frameBytes.subarray(10);
    if (compressionFlags === 0xff) return { ...header, body: '<zstd, не разбирается>' };

    try {
      if (compressionFlags === 0) return { ...header, body: maskValue(decodeMessagePack(bodyBytes)) };

      // Флаги 1..254 означают LZ4. Если после распаковки MessagePack не сходится, пробуем пропустить первые
      // 4 байта: в некоторых реализациях перед данными стоит длина распакованного блока.
      try {
        return { ...header, lz4: true, body: maskValue(decodeMessagePack(decodeLz4Block(bodyBytes))) };
      } catch {
        return { ...header, lz4: true, skippedFirst4Bytes: true, body: maskValue(decodeMessagePack(decodeLz4Block(bodyBytes.subarray(4)))) };
      }
    } catch (error) {
      return { ...header, body: `<ошибка разбора: ${error.message}>` };
    }
  };

  // Приводит данные кадра (Blob, ArrayBuffer или типизированный массив) к Uint8Array. Blob читается асинхронно.
  const toUint8Array = (data) => {
    if (data instanceof Blob) return data.arrayBuffer().then((buffer) => new Uint8Array(buffer));
    if (data instanceof ArrayBuffer) return Promise.resolve(new Uint8Array(data));
    if (ArrayBuffer.isView(data)) return Promise.resolve(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
    return Promise.reject(new Error('неизвестный тип данных'));
  };

  // Записывает один кадр. direction: 'out' (отправили мы) или 'in' (пришёл от сервера).
  const recordFrame = (direction, socket, data) => {
    let socketAddress = '';
    try {
      const url = new URL(socket.url);
      socketAddress = url.host + url.pathname;
    } catch { /* адрес не разобрался: записываем без него */ }

    // Запись добавляется сразу, чтобы сохранить порядок кадров; содержимое бинарного кадра дописывается позже.
    const record = {
      index: recordedFrames.length,
      elapsedMs: Math.round(performance.now() - recordingStartedAtMs),
      direction,
      socketAddress,
      frameKind: '',
      frame: null,
    };
    recordedFrames.push(record);

    if (typeof data === 'string') {
      record.frameKind = 'text';
      try {
        const textFrame = JSON.parse(data);
        record.frame = { ...textFrame, payload: maskValue(textFrame.payload) };
      } catch {
        record.frame = `<текст не в формате JSON, ${data.length} символов>`;
      }
      return;
    }

    record.frameKind = 'binary';
    toUint8Array(data)
      .then((frameBytes) => { record.frame = decodeBinaryFrame(frameBytes); })
      .catch((error) => { record.frame = `<не удалось прочитать: ${error.message}>`; });
  };

  // Исходящие кадры. Метод send живёт в прототипе WebSocket, поэтому перехват действует и на уже открытый сокет.
  let lastSeenSocket = null;
  const originalSend = WebSocket.prototype.send;
  WebSocket.prototype.send = function (data) {
    lastSeenSocket = this; // запоминаем сокет, чтобы можно было вызвать reconnect()
    try { recordFrame('out', this, data); } catch { /* запись не должна ломать сайт */ }
    return originalSend.call(this, data);
  };

  // Входящие кадры. Подменяем геттер MessageEvent.data: сайт вызывает его, когда читает полученное сообщение.
  const originalDataProperty = Object.getOwnPropertyDescriptor(MessageEvent.prototype, 'data');
  const alreadyRecordedEvents = new WeakSet(); // одно событие сайт может прочитать несколько раз, записываем один
  Object.defineProperty(MessageEvent.prototype, 'data', {
    configurable: true,
    get() {
      const data = originalDataProperty.get.call(this);
      try {
        if (this.target instanceof WebSocket && !alreadyRecordedEvents.has(this)) {
          alreadyRecordedEvents.add(this);
          recordFrame('in', this.target, data);
        }
      } catch { /* запись не должна ломать сайт */ }
      return data;
    },
  });

  // ---------- HTTP-запросы (XMLHttpRequest и fetch) ----------

  // Писать ли только запросы, которые меняют данные (всё, кроме GET, HEAD и OPTIONS). Обычных запросов у сайта очень
  // много (картинки, скрипты, аналитика), а нас интересуют загрузки. Поставьте false, чтобы писать вообще все запросы.
  const RECORD_ONLY_HTTP_REQUESTS_THAT_CHANGE_DATA = true;

  // Значения этих заголовков не скрываются (имена в нижнем регистре): секретов в них нет, а формат нам и нужен.
  const HEADER_NAMES_WITH_SAFE_VALUES = new Set([
    'content-type', 'content-disposition', 'content-range', 'content-length', 'content-encoding',
    'accept', 'range', 'x-requested-with',
  ]);

  // Значения этих параметров адреса не скрываются.
  const QUERY_PARAMETER_NAMES_WITH_SAFE_VALUES = new Set(['clientType']);

  const shouldRecordHttpRequest = (method) => {
    if (!RECORD_ONLY_HTTP_REQUESTS_THAT_CHANGE_DATA) return true;
    return !['GET', 'HEAD', 'OPTIONS'].includes(String(method).toUpperCase());
  };

  // Адрес запроса: хост и путь как есть, значения параметров скрыты. Целые числа становятся псевдонимами
  // (те же, что в WebSocket-кадрах), поэтому, например, id файла в адресе можно сопоставить с fileId из кадра.
  const maskUrl = (urlText) => {
    try {
      const url = new URL(urlText, window.location.href);
      const queryParameters = {};
      for (const [name, value] of url.searchParams) {
        if (QUERY_PARAMETER_NAMES_WITH_SAFE_VALUES.has(name)) queryParameters[name] = value;
        else if (/^-?\d+$/.test(value)) queryParameters[name] = maskValue(BigInt(value), name);
        else queryParameters[name] = `<str:${value.length}>`;
      }
      return { address: url.host + url.pathname, queryParameters };
    } catch {
      return `<адрес не разобрался: ${String(urlText).length} символов>`;
    }
  };

  // headerEntries: список пар [имя, значение].
  const maskHeaders = (headerEntries) => {
    const maskedHeaders = {};
    for (const [name, value] of headerEntries) {
      const isSafe = HEADER_NAMES_WITH_SAFE_VALUES.has(String(name).toLowerCase());
      maskedHeaders[name] = isSafe ? value : `<str:${String(value).length}>`;
    }
    return maskedHeaders;
  };

  // Только описание тела запроса: вид, размер и тип. Само содержимое (в том числе файла) в запись не попадает.
  const describeRequestBody = (body) => {
    if (body === null || body === undefined) return null;
    if (typeof body === 'string') return { kind: 'text', length: body.length };
    if (typeof File !== 'undefined' && body instanceof File) return { kind: 'File', byteLength: body.size, type: body.type };
    if (body instanceof Blob) return { kind: 'Blob', byteLength: body.size, type: body.type };
    if (body instanceof ArrayBuffer) return { kind: 'ArrayBuffer', byteLength: body.byteLength };
    if (ArrayBuffer.isView(body)) return { kind: body.constructor.name, byteLength: body.byteLength };
    if (body instanceof FormData) {
      const fields = [...body.entries()].map(([name, value]) => (value instanceof Blob
        ? { name, kind: 'file', byteLength: value.size, type: value.type }
        : { name, kind: 'text', length: String(value).length }));
      return { kind: 'FormData', fields };
    }
    if (body instanceof URLSearchParams) return { kind: 'URLSearchParams', parameterNames: [...body.keys()] };
    if (typeof ReadableStream !== 'undefined' && body instanceof ReadableStream) return { kind: 'ReadableStream' };
    return { kind: Object.prototype.toString.call(body) };
  };

  // Тело ответа, пришедшее текстом: JSON показывается с маскированием, остальное только длиной.
  const describeResponseText = (contentType, text) => {
    if (/json/i.test(contentType)) {
      try { return maskValue(JSON.parse(text)); } catch { /* не JSON: опишем длиной */ }
    }
    return `<текст ${text.length} символов>`;
  };

  // Записывает исходящий HTTP-запрос сразу при отправке; ответ дописывается позже функцией fillHttpResponse.
  const recordHttpRequest = (method, url, headerEntries, body) => {
    const record = {
      index: recordedFrames.length,
      elapsedMs: Math.round(performance.now() - recordingStartedAtMs),
      direction: 'out',
      frameKind: 'http',
      method: String(method).toUpperCase(),
      url: maskUrl(url),
      requestHeaders: maskHeaders(headerEntries),
      requestBody: describeRequestBody(body),
      response: null,
    };
    recordedFrames.push(record);
    return record;
  };

  const fillHttpResponse = (record, status, statusText, headerEntries, body) => {
    record.response = {
      respondedAfterMs: Math.round(performance.now() - recordingStartedAtMs) - record.elapsedMs,
      status,
      statusText,
      headers: maskHeaders(headerEntries),
      body,
    };
  };

  // Из строки заголовков ответа XMLHttpRequest («Имя: значение» на каждой строке) делает список пар [имя, значение].
  const parseRawHeaders = (rawHeaders) => rawHeaders
    .split(/\r?\n/)
    .filter((line) => line.includes(':'))
    .map((line) => {
      const colonIndex = line.indexOf(':');
      return [line.slice(0, colonIndex).trim(), line.slice(colonIndex + 1).trim()];
    });

  // Тело ответа XMLHttpRequest: зависит от того, какой responseType запросил сайт.
  const describeXhrResponseBody = (xhr) => {
    const contentType = xhr.getResponseHeader('content-type') ?? '';
    switch (xhr.responseType) {
      case '':
      case 'text': return describeResponseText(contentType, xhr.responseText);
      case 'json': return maskValue(xhr.response);
      case 'arraybuffer': return { kind: 'ArrayBuffer', byteLength: xhr.response ? xhr.response.byteLength : 0 };
      case 'blob': return { kind: 'Blob', byteLength: xhr.response ? xhr.response.size : 0, type: contentType };
      default: return `<тело ответа типа ${xhr.responseType}>`;
    }
  };

  // XMLHttpRequest: адрес и метод известны в open, заголовки собираются в setRequestHeader, тело приходит в send.
  const xhrRequestDetails = new WeakMap();
  const originalXhrOpen = XMLHttpRequest.prototype.open;
  const originalXhrSetRequestHeader = XMLHttpRequest.prototype.setRequestHeader;
  const originalXhrSend = XMLHttpRequest.prototype.send;

  XMLHttpRequest.prototype.open = function (method, url, ...otherArguments) {
    xhrRequestDetails.set(this, { method: String(method), url: String(url), headerEntries: [] });
    return originalXhrOpen.call(this, method, url, ...otherArguments);
  };

  XMLHttpRequest.prototype.setRequestHeader = function (name, value) {
    const details = xhrRequestDetails.get(this);
    if (details) details.headerEntries.push([name, value]);
    return originalXhrSetRequestHeader.call(this, name, value);
  };

  XMLHttpRequest.prototype.send = function (body) {
    try {
      const details = xhrRequestDetails.get(this);
      if (details && shouldRecordHttpRequest(details.method)) {
        const record = recordHttpRequest(details.method, details.url, details.headerEntries, body);
        this.addEventListener('loadend', () => {
          try {
            fillHttpResponse(record, this.status, this.statusText, parseRawHeaders(this.getAllResponseHeaders()), describeXhrResponseBody(this));
          } catch { /* запись не должна ломать сайт */ }
        });
      }
    } catch { /* запись не должна ломать сайт */ }
    return originalXhrSend.call(this, body);
  };

  // fetch: метод, адрес, заголовки и тело берём из аргументов вызова; ответ читаем с копии, чтобы не мешать сайту.
  const originalFetch = window.fetch;
  if (originalFetch) {
    window.fetch = function (input, init) {
      let record = null;
      try {
        const request = input instanceof Request ? input : null;
        const method = (init && init.method) || (request && request.method) || 'GET';
        if (shouldRecordHttpRequest(method)) {
          const headerEntries = [...new Headers((init && init.headers) || (request && request.headers) || [])];
          record = recordHttpRequest(method, request ? request.url : String(input), headerEntries, init && init.body);
        }
      } catch { /* запись не должна ломать сайт */ }

      const responsePromise = originalFetch.apply(this, arguments);
      if (record) {
        responsePromise.then(
          async (response) => {
            try {
              const contentType = response.headers.get('content-type') ?? '';
              // Читаем только текстовые ответы: большие файлы дважды скачивать не нужно.
              const body = /json|text/i.test(contentType)
                ? describeResponseText(contentType, await response.clone().text())
                : `<тело типа ${contentType || 'неизвестного'}, не читается>`;
              fillHttpResponse(record, response.status, response.statusText, [...response.headers], body);
            } catch { /* запись не должна ломать сайт */ }
          },
          (error) => { record.response = { error: String((error && error.message) || error) }; },
        );
      }
      return responsePromise;
    };
  }

  window.__maxTrafficRecorder = {
    frames: recordedFrames,

    // Все кадры (начиная с fromIndex) одной строкой JSON без переводов строк: удобно копировать через copy(...).
    dump: (fromIndex = 0) => JSON.stringify(recordedFrames.slice(fromIndex)),

    // Печатает кадры в консоль по одному, как раскрывающиеся объекты: удобно читать глазами.
    show: (fromIndex = 0) => {
      recordedFrames.slice(fromIndex).forEach((record) => console.log(record));
      return `показано кадров: ${Math.max(recordedFrames.length - fromIndex, 0)}`;
    },

    clear: () => { recordedFrames.length = 0; },

    // Закрывает текущий сокет: сайт переподключится, и мы запишем рукопожатие и вход по токену.
    reconnect: () => {
      if (lastSeenSocket) {
        lastSeenSocket.close();
        return 'сокет закрыт';
      }
      return 'сокет ещё не найден';
    },
  };
  return 'установлена';
})();
