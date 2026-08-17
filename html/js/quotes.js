/**
 * 汇率行情模块 v2 — 8 国货币兑 CNY
 * 数据源:
 *   今日: fawazahmed0/currency-api (jsdelivr CDN)
 *   历史: frankfurter.dev (5种) + fawazahmed0 版本采样 (3种)
 * 功能: 今日汇率·今年波动·本月波动·一年折线图
 */
const quotesApp = {
  // 8 种货币配置
  currencies: [
    { code: 'MYR', name: '马来西亚', flag: '🇲🇾', region: 'SEA' },
    { code: 'PHP', name: '菲律宾',   flag: '🇵🇭', region: 'SEA' },
    { code: 'THB', name: '泰国',     flag: '🇹🇭', region: 'SEA' },
    { code: 'VND', name: '越南',     flag: '🇻🇳', region: 'SEA' },
    { code: 'IDR', name: '印尼',     flag: '🇮🇩', region: 'SEA' },
    { code: 'SGD', name: '新加坡',   flag: '🇸🇬', region: 'SEA' },
    { code: 'KZT', name: '哈萨克斯坦', flag: '🇰🇿', region: 'CIS' },
    { code: 'KES', name: '肯尼亚',   flag: '🇰🇪', region: 'AFR' },
  ],
  // frankfurter.dev 支持的货币 (5种)
  ffCodes: ['MYR', 'THB', 'SGD', 'PHP', 'IDR'],

  state: {
    today: null,          // { code: rateInv } 1外币 = X CNY
    history: {},          // { code: [{date, rateInv}, ...] }
    ytd: {},              // { code: pct }
    mtd: {},              // { code: pct }
    loaded: false,
    loading: false,
  },

  // ── 入口 ──────────────────────────────────────
  async refresh() {
    if (this.state.loading) return;
    this.state.loading = true;
    this._setStatus('⏳ 加载汇率数据...');
    this.state.loaded = false;
    try {
      // 并行启动三个数据源
      const [todayData, ffHistory, sampledHistory] = await Promise.all([
        this._fetchToday(),
        this._fetchFrankfurterHistory(),
        this._fetchSampledHistory(),
      ]);
      this._processData(todayData, ffHistory, sampledHistory);
      this.state.loaded = true;
      this._setStatus('✅ 已更新');
      this._render();
    } catch (e) {
      console.error('quotesApp error:', e);
      this._setStatus('❌ 加载失败: ' + e.message);
    }
    this.state.loading = false;
  },

  // ── 数据获取 ──────────────────────────────────
  /** 今日实时汇率 — fawazahmed0/currency-api */
  async _fetchToday() {
    const url = 'https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/cny.json';
    const res = await fetch(url);
    if (!res.ok) throw new Error(`Today API ${res.status}`);
    const data = await res.json();
    const cny = data.cny;
    // 返回 1外币 = X 人民币
    const result = {};
    for (const c of this.currencies) {
      const code = c.code.toLowerCase();
      if (cny[code] != null) {
        result[c.code] = 1 / cny[code];
      }
    }
    return result;
  },

  /** frankfurter.dev 批量历史 — 支持5种: MYR THB SGD PHP IDR */
  async _fetchFrankfurterHistory() {
    const end = this._todayStr();
    const start = this._yearAgoStr();
    const symbols = this.ffCodes.join(',');
    const url = `https://api.frankfurter.dev/v1/${start}..${end}?base=CNY&symbols=${symbols}`;
    const res = await fetch(url);
    if (!res.ok) throw new Error(`Frankfurter ${res.status}`);
    const data = await res.json();
    const rates = data.rates || {};
    // 转换为 1外币 = X CNY 格式
    const history = {};
    for (const code of this.ffCodes) history[code] = [];
    const dates = Object.keys(rates).sort();
    for (const date of dates) {
      for (const code of this.ffCodes) {
        const rate = rates[date]?.[code];
        if (rate != null) {
          history[code].push({ date, rateInv: 1 / rate });
        }
      }
    }
    return history;
  },

  /** jsdelivr 版本采样 — 用于 VND/KZT/KES */
  async _fetchSampledHistory() {
    const needCodes = this.currencies
      .filter(c => !this.ffCodes.includes(c.code))
      .map(c => c.code.toLowerCase());
    if (needCodes.length === 0) return {};

    // 1. 获取版本列表
    const listUrl = 'https://data.jsdelivr.com/v1/packages/npm/@fawazahmed0/currency-api';
    const listRes = await fetch(listUrl);
    if (!listRes.ok) throw new Error(`Version list ${listRes.status}`);
    const listData = await listRes.json();
    const versions = listData.versions || [];

    // 2. 筛选过去一年内的版本
    const cutoff = new Date();
    cutoff.setFullYear(cutoff.getFullYear() - 1);
    const filtered = versions.filter(v => {
      const d = this._parseVersionDate(v.version);
      return d && d >= cutoff;
    }).reverse(); // 旧到新排序

    if (filtered.length === 0) return {};

    // 3. 采样：每 14 天取一个点 (约 26 个点)
    const sampled = [];
    let lastDate = null;
    for (const v of filtered) {
      const d = this._parseVersionDate(v.version);
      if (!d) continue;
      if (!lastDate || (d - lastDate) >= 14 * 86400000) {
        sampled.push(v);
        lastDate = d;
      }
    }
    // 确保包含最新一天
    const latest = filtered[filtered.length - 1];
    if (sampled[sampled.length - 1]?.version !== latest.version) {
      sampled.push(latest);
    }

    // 4. 并行拉取 (6并发)
    const history = {};
    for (const code of needCodes) history[code.toUpperCase()] = [];

    const batchSize = 6;
    for (let i = 0; i < sampled.length; i += batchSize) {
      const batch = sampled.slice(i, i + batchSize);
      const results = await Promise.all(
        batch.map(v => this._fetchVersionCny(v.version))
      );
      for (let j = 0; j < batch.length; j++) {
        const cnyData = results[j];
        if (!cnyData) continue;
        const date = batch[j].version.replace(/\./g, '-');
        for (const code of needCodes) {
          if (cnyData[code] != null) {
            history[code.toUpperCase()].push({ date, rateInv: 1 / cnyData[code] });
          }
        }
      }
    }
    return history;
  },

  async _fetchVersionCny(version) {
    try {
      const url = `https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@${version}/v1/currencies/cny.json`;
      const res = await fetch(url);
      if (!res.ok) return null;
      const data = await res.json();
      return data.cny || null;
    } catch {
      return null;
    }
  },

  // ── 数据处理 ──────────────────────────────────
  _processData(today, ffHistory, sampledHistory) {
    this.state.today = today;
    // 合并历史数据
    this.state.history = {};
    const allHistory = { ...ffHistory, ...sampledHistory };
    for (const code of this.currencies.map(c => c.code)) {
      this.state.history[code] = allHistory[code] || [];
    }
    // 计算波动
    this.state.ytd = this._calcChange('year');
    this.state.mtd = this._calcChange('month');
  },

  /** 计算今年/本月波动百分比 */
  _calcChange(period) {
    const result = {};
    const now = new Date();
    const periodStart = period === 'year'
      ? new Date(now.getFullYear(), 0, 1)     // 今年1月1日
      : new Date(now.getFullYear(), now.getMonth(), 1); // 本月1日

    for (const c of this.currencies) {
      const code = c.code;
      const todayRate = this.state.today[code];
      if (todayRate == null) { result[code] = null; continue; }

      // 从历史数据中找最接近 periodStart 的日期
      const hist = this.state.history[code] || [];
      let closestRate = null;
      let closestDiff = Infinity;
      for (const h of hist) {
        const hDate = new Date(h.date);
        const diff = Math.abs(hDate - periodStart);
        if (diff < closestDiff && hDate <= now) {
          closestDiff = diff;
          closestRate = h.rateInv;
        }
      }

      if (closestRate != null) {
        result[code] = (todayRate - closestRate) / closestRate * 100;
      } else {
        result[code] = null;
      }
    }
    return result;
  },

  // ── 渲染 ──────────────────────────────────────
  _render() {
    const container = document.getElementById('quotes-table-container');
    if (!container) return;
    const now = new Date();
    const updateEl = document.getElementById('quotes-update-time');
    if (updateEl) {
      updateEl.textContent = `更新: ${now.toLocaleString('zh-CN')}`;
    }

    let html = '<div class="quotes-table-wrap"><table class="quotes-table">';
    html += '<thead><tr>' +
      '<th>货币</th>' +
      '<th>汇率</th>' +
      '<th>今年↑↓</th>' +
      '<th>本月↑↓</th>' +
      '<th>一年趋势</th>' +
      '</tr></thead><tbody>';

    for (const c of this.currencies) {
      const code = c.code;
      const rate = this.state.today[code];
      const ytd = this.state.ytd[code];
      const mtd = this.state.mtd[code];
      const hist = this.state.history[code] || [];

      const rateStr = rate != null ? rate.toFixed(4) : '—';
      const ytdStr = this._fmtPct(ytd);
      const mtdStr = this._fmtPct(mtd);
      const chartId = `spark-${code}`;

      html += `<tr>
        <td class="quotes-ccy">${c.flag} ${c.name}<br><span class="quotes-code">${code}</span></td>
        <td class="quotes-rate">1 ${code} =<br><span class="quotes-price">${rateStr}</span></td>
        <td class="quotes-chg">${ytdStr}</td>
        <td class="quotes-chg">${mtdStr}</td>
        <td class="quotes-chart"><canvas id="${chartId}" width="180" height="44"></canvas></td>
      </tr>`;
    }

    html += '</tbody></table></div>';
    container.innerHTML = html;

    // 绘制折线图
    requestAnimationFrame(() => {
      for (const c of this.currencies) {
        const hist = this.state.history[c.code] || [];
        if (hist.length >= 2) {
          this._drawSparkline(`spark-${c.code}`, hist);
        }
      }
    });
  },

  _drawSparkline(canvasId, data) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    const W = canvas.width, H = canvas.height;
    const pad = { top: 4, bottom: 4, left: 2, right: 2 };
    const plotW = W - pad.left - pad.right;
    const plotH = H - pad.top - pad.bottom;

    // 数据准备
    const values = data.map(d => d.rateInv);
    const min = Math.min(...values);
    const max = Math.max(...values);
    const range = max - min || 1;

    // 颜色: 整体趋势
    const firstVal = values[0];
    const lastVal = values[values.length - 1];
    const isUp = lastVal >= firstVal;
    const lineColor = isUp ? '#dc3545' : '#28a745';
    // 红色↑=外币升值, 绿色↓=外币贬值

    ctx.clearRect(0, 0, W, H);

    // 网格线（浅灰）
    ctx.strokeStyle = '#eee';
    ctx.lineWidth = 0.5;
    for (let y = 0; y < 4; y++) {
      const yy = pad.top + (plotH / 3) * y;
      ctx.beginPath();
      ctx.moveTo(pad.left, yy);
      ctx.lineTo(W - pad.right, yy);
      ctx.stroke();
    }

    // 折线
    ctx.strokeStyle = lineColor;
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    for (let i = 0; i < values.length; i++) {
      const x = pad.left + (i / (values.length - 1)) * plotW;
      const y = pad.top + plotH - ((values[i] - min) / range) * plotH;
      if (i === 0) ctx.moveTo(x, y);
      else ctx.lineTo(x, y);
    }
    ctx.stroke();

    // 起点终点圆点
    ctx.fillStyle = '#999';
    ctx.beginPath();
    ctx.arc(pad.left, pad.top + plotH - ((values[0] - min) / range) * plotH, 2, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = lineColor;
    ctx.beginPath();
    ctx.arc(W - pad.right, pad.top + plotH - ((values[values.length - 1] - min) / range) * plotH, 2, 0, Math.PI * 2);
    ctx.fill();
  },

  // ── 工具 ──────────────────────────────────────
  _fmtPct(val) {
    if (val == null) return '<span class="chg-na">—</span>';
    const arrow = val >= 0 ? '↑' : '↓';
    const color = val >= 0 ? '#dc3545' : '#28a745';
    return `<span style="color:${color};font-weight:600">${arrow}${Math.abs(val).toFixed(2)}%</span>`;
  },

  _todayStr() {
    const d = new Date();
    return d.toISOString().slice(0, 10);
  },

  _yearAgoStr() {
    const d = new Date();
    d.setFullYear(d.getFullYear() - 1);
    return d.toISOString().slice(0, 10);
  },

  _parseVersionDate(vStr) {
    // vStr = "2026.8.15"
    const parts = vStr.split('.').map(Number);
    if (parts.length !== 3) return null;
    return new Date(parts[0], parts[1] - 1, parts[2]);
  },

  _setStatus(msg) {
    const el = document.getElementById('quotes-status');
    if (el) el.textContent = msg;
  },

  // ── 墨水屏推送 ──────────────────────────────────
  /** 将汇率表渲染为位图并推送至墨水屏 */
  async pushToEpd() {
    if (!this.state.loaded) {
      alert('汇率数据尚未加载，请稍后重试');
      return;
    }
    // 检查 BLE 连接 (全局变量来自 main.js)
    if (typeof epdCharacteristic === 'undefined' || !epdCharacteristic) {
      this._setStatus('⚠️ 请先点击"连接"配对联接墨水屏设备');
      return;
    }

    // 获取当前画布尺寸
    const sel = document.getElementById('canvasSize');
    const sizeName = sel.value;
    const size = (typeof canvasSizes !== 'undefined')
      ? canvasSizes.find(s => s.name === sizeName) : null;
    if (!size) {
      alert('未找到画布尺寸配置，请先选择墨水屏型号');
      return;
    }
    const W = size.width, H = size.height;

    // 离线 Canvas 渲染
    const offscreen = document.createElement('canvas');
    offscreen.width = W;
    offscreen.height = H;
    const ctx = offscreen.getContext('2d');
    this._renderEpdImage(ctx, W, H);

    // 转换为 EPD 位图数据（按驱动颜色模式）
    const imageData = ctx.getImageData(0, 0, W, H);
    const driverSelect = document.getElementById('epddriver');
    const colorMode = driverSelect?.options[driverSelect.selectedIndex]
      ?.getAttribute('data-color') || 'blackWhiteColor';

    let bwData, redData = null;
    if (colorMode === 'threeColor') {
      const processedData = processImageData(imageData, 'threeColor');
      const half = Math.floor(processedData.length / 2);
      bwData = processedData.slice(0, half);
      redData = processedData.slice(half);
    } else {
      bwData = processImageData(imageData, 'blackWhiteColor');
    }

    // BLE 发送
    this._setStatus('📤 正在推送至墨水屏...');
    try {
      await write(EpdCmd.INIT);

      // 发送黑白通道
      await writeImage(bwData, 'bw');

      // 三色屏：发送红色通道（上涨数据用红色显示）
      if (redData) {
        await writeImage(redData, 'red');
      }

      await write(EpdCmd.REFRESH);
      this._setStatus('✅ 推送完成！墨水屏刷新中...');
    } catch (e) {
      console.error('pushToEpd error:', e);
      this._setStatus('❌ 推送失败: ' + e.message);
    }
  },

  /** 在 Canvas 上绘制汇率表 (纯黑白，4.2寸 400x300 优化布局) */
  _renderEpdImage(ctx, W, H) {
    const now = new Date();
    const dateStr = now.toLocaleDateString('zh-CN');

    // 白背景
    ctx.fillStyle = '#FFFFFF';
    ctx.fillRect(0, 0, W, H);
    ctx.fillStyle = '#000000';

    // 标题
    ctx.textAlign = 'center';
    ctx.font = 'bold 18px sans-serif';
    ctx.fillText(`💰 汇率 CNY · ${dateStr}`, W / 2, 18);

    // 分隔线
    ctx.fillRect(4, 22, W - 8, 2);

    // 行高：标题区约26px，剩余分给8行
    const baseY = 24;
    const rowH = Math.floor((H - baseY - 4) / 8);
    if (rowH < 28) return; // 空间不足

    // 固定字体（墨水屏尽可能大）
    const fontSize = 18;      // 主数值 bold
    const pctFont = 16;       // 百分比

    // 列位置（按实际渲染宽度: code=44px, rate=62px, pct=68px, 间隙3px）
    // 实测渲染: MYR=44px, 0.0003=62px, ↓4.35%=68px
    const colCode = 4;
    const colRate = 51;           // 4+44+3
    const colYoy = 116;           // 51+62+3
    const colMom = 187;           // 116+68+3
    const sparkX = 258;           // 187+68+3
    const sparkW = Math.max(50, W - sparkX - 8); // 134px
    const sparkH = rowH - 6;

    for (let i = 0; i < this.currencies.length; i++) {
      const c = this.currencies[i];
      const code = c.code;
      const rate = this.state.today[code];
      const ytd = this.state.ytd[code];
      const mtd = this.state.mtd[code];
      const hist = this.state.history[code] || [];
      const y = baseY + i * rowH;
      const textY = y + rowH * 0.72;

      // 货币代码
      ctx.textAlign = 'left';
      ctx.font = `bold ${fontSize}px sans-serif`;
      ctx.fillText(code, colCode, textY);

      // 汇率 (统一4位小数，避免小币种6位数字在16px下溢出)
      const rateStr = rate != null ? rate.toFixed(4) : '—';
      ctx.fillText(rateStr, colRate, textY);

      // 今年波动（在列上方留出折线图空间，文本集中底部）
      ctx.font = `${pctFont}px sans-serif`;
      if (ytd != null) {
        ctx.fillStyle = ytd >= 0 ? '#FF0000' : '#000000';
        ctx.fillText(`${ytd >= 0 ? '↑' : '↓'}${Math.abs(ytd).toFixed(2)}%`, colYoy, textY);
      }
      // 本月波动
      if (mtd != null) {
        ctx.fillStyle = mtd >= 0 ? '#FF0000' : '#000000';
        ctx.fillText(`${mtd >= 0 ? '↑' : '↓'}${Math.abs(mtd).toFixed(2)}%`, colMom, textY);
      }
      // 恢复黑色
      ctx.fillStyle = '#000000';

      // 折线图（独立列，与文本物理隔离）
      if (hist.length >= 2 && sparkW > 20) {
        this._drawEpdSparkline(ctx, sparkX, y + 3, sparkW, sparkH, hist);
      }
    }
  },

  /** 在 Canvas 上绘制折线图 (纯黑白) */
  _drawEpdSparkline(ctx, x, y, w, h, data) {
    const values = data.map(d => d.rateInv);
    const min = Math.min(...values);
    const max = Math.max(...values);
    const range = max - min || 1;

    ctx.save();
    ctx.beginPath();
    ctx.rect(x, y, w, h);
    ctx.clip();

    // 折线
    ctx.strokeStyle = '#000';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    for (let i = 0; i < values.length; i++) {
      const px = x + (i / Math.max(1, values.length - 1)) * w;
      const py = y + h - ((values[i] - min) / range) * h;
      if (i === 0) ctx.moveTo(px, py);
      else ctx.lineTo(px, py);
    }
    ctx.stroke();

    // 起点/终点小圆点（强化趋势方向）
    ctx.fillStyle = '#000';
    const firstX = x;
    const firstY = y + h - ((values[0] - min) / range) * h;
    ctx.beginPath();
    ctx.arc(firstX, firstY, 1.5, 0, Math.PI * 2);
    ctx.fill();
    const lastX = x + w;
    const lastY = y + h - ((values[values.length - 1] - min) / range) * h;
    ctx.beginPath();
    ctx.arc(lastX, lastY, 1.5, 0, Math.PI * 2);
    ctx.fill();

    ctx.restore();
  },
};

// 自动初始化
document.addEventListener('DOMContentLoaded', () => {
  if (document.getElementById('quotes-panel')) {
    quotesApp.refresh();
    // 每 5 分钟自动刷新
    setInterval(() => quotesApp.refresh(), 5 * 60 * 1000);
  }
});