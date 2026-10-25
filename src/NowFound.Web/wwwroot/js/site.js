(() => {
  const root = document.documentElement;

  // ---- 表示言語に合わせた文言（レイアウトが #i18n-data に Js.* キーを JSON で埋め込む） ----
  // 使い方: t('Js.Key', 引数...)。文言中の {0} {1} … を引数で置き換える。キーが無ければキーをそのまま返す。
  const messages = (() => {
    try {
      return JSON.parse(document.getElementById('i18n-data').textContent);
    } catch (error) {
      return {};
    }
  })();
  const t = (key, ...args) => (messages[key] || key)
    .replace(/\{(\d+)\}/g, (match, index) => (args[Number(index)] === undefined ? match : String(args[Number(index)])));

  // localStorage が使えない環境（プライベートモード等）でも動くようにする。
  const storage = {
    get(key) {
      try {
        return window.localStorage.getItem(key);
      } catch (error) {
        return null;
      }
    },
    set(key, value) {
      try {
        window.localStorage.setItem(key, value);
      } catch (error) {
        // 保存できない環境では状態を記憶しないだけにする。
      }
    },
  };

  // ---- グローバルナビゲーションの開閉（ハンバーガー） ----
  const navToggle = document.getElementById('nav-toggle');
  if (navToggle) {
    const syncNavToggle = () => {
      navToggle.setAttribute('aria-expanded', String(!root.classList.contains('nav-collapsed')));
    };
    syncNavToggle();
    navToggle.addEventListener('click', () => {
      const collapsed = root.classList.toggle('nav-collapsed');
      syncNavToggle();
      storage.set('nav-collapsed', collapsed ? '1' : '0');
    });
  }

  // 狭い画面ではドロワーがオーバーレイ表示になるため、スクリムのクリックや Esc で閉じられるようにする。
  const isNarrow = () => window.matchMedia('(max-width: 840px)').matches;
  const closeNav = () => {
    root.classList.add('nav-collapsed');
    if (navToggle) {
      navToggle.setAttribute('aria-expanded', 'false');
    }
  };
  const scrim = document.getElementById('nav-scrim');
  if (scrim) {
    scrim.addEventListener('click', closeNav);
  }
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && isNarrow() && !root.classList.contains('nav-collapsed')) {
      closeNav();
    }
  });

  // ---- テーマ選択のプレビュー（保存前でも選んだテーマを画面に反映する） ----
  const themeGrid = document.getElementById('theme-grid');
  if (themeGrid) {
    themeGrid.addEventListener('change', (event) => {
      if (event.target.name === 'Theme') {
        root.dataset.theme = event.target.value;
      }
    });
  }

  // 削除などの確認が必要なフォームの送信前に確認ダイアログを出す。
  document.querySelectorAll('form[data-confirm]').forEach((form) => {
    form.addEventListener('submit', (event) => {
      if (!window.confirm(form.dataset.confirm)) {
        event.preventDefault();
      }
    });
  });

  // ---- 検索条件行 ----
  const form = document.getElementById('search-form');
  const extraConditions = document.getElementById('extra-conditions');
  const template = document.getElementById('condition-template');
  const addButton = document.getElementById('add-condition');
  const detailToggle = document.getElementById('detail-toggle');
  const detailPanel = document.getElementById('detail-panel');
  const isDateField = (value) => value === 'ModifiedDate' || value === 'CreatedDate';

  // 対象の種類に応じて、キーワード / 日付範囲 / サイズ（比較・数値・単位）のどれかだけを表示する。
  const syncRow = (row) => {
    const field = row.querySelector('[data-field]').value;
    const hideText = isDateField(field) || field === 'Size';
    const textWrap = row.querySelector('[data-text-wrap]');
    if (textWrap) {
      textWrap.hidden = hideText;
    }
    const textInput = row.querySelector('[data-text]');
    if (textInput) {
      textInput.hidden = hideText;
    }
    row.querySelector('[data-dates]').hidden = !isDateField(field);
    row.querySelector('[data-size]').hidden = field !== 'Size';
  };

  // 行の追加・削除後に name の添字を DOM 順の連番へ振り直す（先頭は検索バー内の行）。
  const renumber = () => {
    form.querySelectorAll('[data-condition]').forEach((row, index) => {
      row.querySelectorAll('[name]').forEach((element) => {
        element.name = element.name.replace(/Conditions\[[^\]]+\]/, `Conditions[${index}]`);
      });
    });
  };

  const bindRow = (row) => {
    row.querySelector('[data-field]').addEventListener('change', () => syncRow(row));
    const textInput = row.querySelector('[data-text]');
    const clearButton = row.querySelector('[data-clear-text]');
    if (textInput && clearButton) {
      const syncClear = () => {
        clearButton.hidden = !textInput.value;
      };
      textInput.addEventListener('input', syncClear);
      textInput.addEventListener('change', syncClear);
      clearButton.addEventListener('click', () => {
        textInput.value = '';
        syncClear();
        textInput.focus();
        textInput.dispatchEvent(new Event('input', { bubbles: true }));
        textInput.dispatchEvent(new Event('change', { bubbles: true }));
      });
      syncClear();
    }
    const remove = row.querySelector('[data-remove]');
    if (remove) {
      remove.addEventListener('click', () => {
        row.remove();
        renumber();
      });
    }
    syncRow(row);
  };

  const setDetailOpen = (open) => {
    detailPanel.hidden = !open;
    detailToggle.setAttribute('aria-expanded', String(open));
  };

  if (form && extraConditions && template && addButton) {
    form.querySelectorAll('[data-condition]').forEach(bindRow);
    detailToggle.addEventListener('click', () => setDetailOpen(detailPanel.hidden));
    addButton.addEventListener('click', () => {
      const index = form.querySelectorAll('[data-condition]').length;
      const holder = document.createElement('div');
      holder.innerHTML = template.innerHTML.replaceAll('__INDEX__', String(index)).trim();
      const row = holder.firstElementChild;
      extraConditions.appendChild(row);
      bindRow(row);
    });
  }

  // ---- フォルダツリー（開閉とドリルダウン） ----
  const page = document.getElementById('search-page');
  const treeToggle = document.getElementById('tree-toggle');
  const tree = document.getElementById('folder-tree');

  if (page && treeToggle) {
    const syncTree = () => {
      treeToggle.setAttribute('aria-expanded', String(!page.classList.contains('tree-collapsed')));
    };
    if (storage.get('tree-collapsed') === '1') {
      page.classList.add('tree-collapsed');
    }
    syncTree();
    treeToggle.addEventListener('click', () => {
      const collapsed = page.classList.toggle('tree-collapsed');
      syncTree();
      storage.set('tree-collapsed', collapsed ? '1' : '0');
    });
  }

  const buildSearchUrl = (folderId, path) => {
    const params = form ? new URLSearchParams(new FormData(form)) : new URLSearchParams(window.location.search);
    if (folderId !== null && folderId !== undefined && folderId !== '') {
      params.set('FolderId', String(folderId));
    } else {
      params.delete('FolderId');
    }
    if (path) {
      params.set('FolderPath', path);
    } else {
      params.delete('FolderPath');
    }
    params.delete('Page');
    const base = form?.getAttribute('action') || window.location.pathname;
    const query = params.toString();
    return query ? `${base}?${query}` : base;
  };

  const isFolderLink = (element) => element?.closest('#folder-tree a, .scope a[data-clear-folder="true"], #search-results a[data-folder-id]');

  const updateFolderLinkHref = (link) => {
    if (link.dataset.clearFolder === 'true') {
      link.href = buildSearchUrl(null, null);
    } else if (link.dataset.folderId !== undefined) {
      link.href = buildSearchUrl(link.dataset.folderId, link.dataset.folderPath || null);
    }
  };

  const applyFolderFilter = (link) => {
    if (!form) {
      return false;
    }
    const isClear = link.dataset.clearFolder === 'true';
    const folderId = isClear ? '' : (link.dataset.folderId ?? '');
    const folderPath = isClear ? '' : (link.dataset.folderPath ?? '');
    const folderIdInput = form.querySelector('input[name="FolderId"]');
    const folderPathInput = form.querySelector('input[name="FolderPath"]');
    if (folderIdInput) {
      folderIdInput.value = folderId;
    }
    if (folderPathInput) {
      folderPathInput.value = folderPath;
    }
    if (typeof form.requestSubmit === 'function') {
      form.requestSubmit();
    } else {
      form.submit();
    }
    return true;
  };

  document.addEventListener('pointerdown', (event) => {
    const link = isFolderLink(event.target);
    if (link) {
      updateFolderLinkHref(link);
    }
  });

  document.addEventListener('click', (event) => {
    const link = isFolderLink(event.target);
    if (link && form && event.button === 0 && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey) {
      event.preventDefault();
      applyFolderFilter(link);
    }
  });

  const loadChildren = async (item) => {
    const url = new URL(tree.dataset.url, window.location.origin);
    url.searchParams.set('folderId', item.dataset.folderId);
    if (item.dataset.path) {
      url.searchParams.set('path', item.dataset.path);
    }
    const response = await fetch(url, { headers: { Accept: 'application/json' } });
    if (!response.ok) {
      return [];
    }
    return response.json();
  };

  const createItem = (folderId, child) => {
    const li = document.createElement('li');
    li.dataset.folderId = folderId;
    li.dataset.path = child.path;

    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'toggle';
    toggle.setAttribute('aria-expanded', 'false');
    toggle.setAttribute('aria-label', t('Js.Tree.Expand'));
    toggle.textContent = '▸';

    const link = document.createElement('a');
    link.href = buildSearchUrl(folderId, child.path);
    link.dataset.folderId = folderId;
    link.dataset.folderPath = child.path;
    link.textContent = child.name;

    li.append(toggle, link);
    return li;
  };

  const toggleItem = async (item, button) => {
    const expanded = button.getAttribute('aria-expanded') === 'true';
    let list = item.querySelector(':scope > ul');
    if (expanded) {
      button.setAttribute('aria-expanded', 'false');
      button.textContent = '▸';
      if (list) {
        list.hidden = true;
      }
      return;
    }
    if (!list) {
      list = document.createElement('ul');
      const children = await loadChildren(item);
      children.forEach((child) => list.appendChild(createItem(item.dataset.folderId, child)));
      if (children.length === 0) {
        const empty = document.createElement('li');
        empty.className = 'muted';
        empty.textContent = t('Js.Tree.NoSubfolders');
        list.appendChild(empty);
      }
      item.appendChild(list);
    }
    list.hidden = false;
    button.setAttribute('aria-expanded', 'true');
    button.textContent = '▾';
  };

  if (tree) {
    // フォルダで絞り込むとページが再読み込みされるため、展開状態とスクロール位置をタブ内に記憶して復元する。
    const treeStateKey = 'nowfound.folderTree.expanded';
    const treeScrollKey = 'nowfound.folderTree.scroll';
    const panel = document.getElementById('tree-panel');
    const itemKey = (item) => `${item.dataset.folderId}\u0000${item.dataset.path ?? ''}`;
    const readExpanded = () => {
      try {
        return new Set(JSON.parse(window.sessionStorage.getItem(treeStateKey) || '[]'));
      } catch (error) {
        return new Set();
      }
    };
    const writeExpanded = (keys) => {
      try {
        window.sessionStorage.setItem(treeStateKey, JSON.stringify([...keys]));
      } catch (error) {
        // 記憶できない環境では展開状態を保たないだけにする。
      }
    };

    tree.addEventListener('click', async (event) => {
      const button = event.target.closest('button.toggle');
      if (!button) {
        return;
      }
      const item = button.closest('li');
      await toggleItem(item, button);
      const keys = readExpanded();
      if (button.getAttribute('aria-expanded') === 'true') {
        keys.add(itemKey(item));
      } else {
        keys.delete(itemKey(item));
      }
      writeExpanded(keys);
    });

    const markCurrent = () => {
      const folderId = form?.querySelector('input[name="FolderId"]')?.value ?? '';
      const folderPath = form?.querySelector('input[name="FolderPath"]')?.value ?? '';
      if (!folderPath) {
        return;
      }
      tree.querySelectorAll('a[data-folder-path]').forEach((link) => {
        const matched = link.dataset.folderId === folderId && link.dataset.folderPath === folderPath;
        link.classList.toggle('current', matched);
      });
    };

    const restoreItems = async (list, keys) => {
      for (const item of list.querySelectorAll(':scope > li[data-folder-id]')) {
        const button = item.querySelector(':scope > button.toggle');
        if (button && keys.has(itemKey(item))) {
          await toggleItem(item, button);
          const children = item.querySelector(':scope > ul');
          if (children) {
            await restoreItems(children, keys);
          }
        }
      }
    };

    const restoreTree = async () => {
      const keys = readExpanded();
      if (keys.size > 0) {
        await restoreItems(tree, keys);
      }
      markCurrent();
      if (panel) {
        try {
          const saved = JSON.parse(window.sessionStorage.getItem(treeScrollKey) || 'null');
          if (saved) {
            panel.scrollTop = saved.top;
            panel.scrollLeft = saved.left;
          }
        } catch (error) {
          // 復元できなければ先頭のままにする。
        }
      }
    };

    window.addEventListener('pagehide', () => {
      if (!panel) {
        return;
      }
      try {
        window.sessionStorage.setItem(treeScrollKey, JSON.stringify({ top: panel.scrollTop, left: panel.scrollLeft }));
      } catch (error) {
        // 記憶できない環境では位置を保たないだけにする。
      }
    });

    restoreTree();
  }

  // ---- チェックボックス一覧の絞り込み（グループ編集のメンバー・フォルダ）部分一致のみ ----
  // 非表示にした項目も送信されるため、絞り込み中でもチェック状態は保たれる。
  document.querySelectorAll('[data-check-filter]').forEach((input) => {
    const list = document.querySelector(`[data-check-list="${input.dataset.checkFilter}"]`);
    if (!list) {
      return;
    }
    input.addEventListener('input', () => {
      const keyword = input.value.trim().toLowerCase();
      list.querySelectorAll('label.check').forEach((label) => {
        label.hidden = keyword !== '' && !label.textContent.toLowerCase().includes(keyword);
      });
    });
    // Enter でフォームが送信されないようにする。
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
      }
    });
  });

  // ---- サイドパネルのタブ（フォルダ / 保存した検索） ----
  const tabs = [...document.querySelectorAll('[role="tab"]')];
  tabs.forEach((tab) => {
    tab.addEventListener('click', () => {
      tabs.forEach((other) => {
        const selected = other === tab;
        other.setAttribute('aria-selected', String(selected));
        document.getElementById(other.getAttribute('aria-controls')).hidden = !selected;
      });
    });
  });

  // ---- 検索の保存ダイアログ ----
  const saveDialog = document.getElementById('save-dialog');
  const saveOpen = document.getElementById('save-open');
  if (saveDialog && saveOpen && typeof saveDialog.showModal === 'function') {
    saveOpen.addEventListener('click', () => {
      saveDialog.showModal();
      const name = document.getElementById('save-name');
      name.focus();
      name.select();
    });
    saveDialog.querySelectorAll('[data-close]').forEach((button) => {
      button.addEventListener('click', () => saveDialog.close());
    });
    // ダイアログの外側（背景）をクリックしたら閉じる。
    // ダイアログ自身の余白のクリックと区別するため、座標が枠の外にあるときだけ閉じる。
    saveDialog.addEventListener('click', (event) => {
      if (event.target !== saveDialog) {
        return;
      }
      const rect = saveDialog.getBoundingClientRect();
      const inside = event.clientX >= rect.left && event.clientX <= rect.right
        && event.clientY >= rect.top && event.clientY <= rect.bottom;
      if (!inside) {
        saveDialog.close();
      }
    });
  }

  // ---- ファイルの種類の編集（システム設定）: 行の追加・削除 ----
  const kindBody = document.getElementById('kind-rows');
  const kindTemplate = document.getElementById('kind-row-template');
  const addKindButton = document.getElementById('add-kind');

  // 行の追加・削除後に name の添字（Rows[n]）を表示順の連番へ振り直す。
  const renumberKinds = () => {
    kindBody.querySelectorAll('[data-kind-row]').forEach((row, index) => {
      row.querySelectorAll('[name]').forEach((element) => {
        element.name = element.name.replace(/Rows\[[^\]]+\]/, `Rows[${index}]`);
      });
    });
  };

  if (kindBody && kindTemplate && addKindButton) {
    kindBody.addEventListener('click', (event) => {
      const button = event.target.closest('[data-remove-kind]');
      if (button) {
        button.closest('[data-kind-row]').remove();
        renumberKinds();
      }
    });

    addKindButton.addEventListener('click', () => {
      const fragment = kindTemplate.content.cloneNode(true);
      kindBody.appendChild(fragment);
      renumberKinds();
      const rows = kindBody.querySelectorAll('[data-kind-row]');
      rows[rows.length - 1].querySelector('.icon-input').focus();
    });
  }

  // ---- 結果表の列の表示/非表示（ブラウザに記憶） ----
  // 列の表示/非表示（表の id と、ブラウザへの記憶キーごと）。
  // 検索結果表はインクリメンタルサーチで差し替わるため、差し替えのたびに呼び直せるようにしてある。
  const setupColumns = (tableId, storeKey) => {
    const table = document.getElementById(tableId);
    const columnToggles = document.querySelectorAll(`[data-col-toggle][data-col-table="${tableId}"]`);
    if (!table || columnToggles.length === 0) {
      return;
    }

    const saved = storage.get(storeKey);
    let hidden;
    try {
      hidden = saved === null ? null : new Set(JSON.parse(saved));
    } catch (error) {
      hidden = null;
    }
    if (hidden === null) {
      hidden = new Set(
        [...columnToggles].filter((box) => box.dataset.defaultHidden === 'true').map((box) => box.dataset.colToggle),
      );
    }

    const applyColumns = () => {
      columnToggles.forEach((box) => {
        const col = box.dataset.colToggle;
        const isHidden = hidden.has(col);
        box.checked = !isHidden;
        table.classList.toggle(`hide-col-${col}`, isHidden);
      });
    };

    columnToggles.forEach((box) => {
      box.addEventListener('change', () => {
        if (box.checked) {
          hidden.delete(box.dataset.colToggle);
        } else {
          hidden.add(box.dataset.colToggle);
        }
        storage.set(storeKey, JSON.stringify([...hidden]));
        applyColumns();
      });
    });
    applyColumns();
  };
  setupColumns('result-table', 'result-columns');
  setupColumns('folder-table', 'folder-columns');
  setupColumns('user-table', 'user-columns');
  setupColumns('group-table', 'group-columns');
  setupColumns('log-table', 'log-columns');

  // ---- フルパスのクリップボードコピー ----
  // http（非セキュアコンテキスト）では navigator.clipboard が使えないため execCommand にフォールバックする。
  const copyText = async (text) => {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
      return;
    }
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    const succeeded = document.execCommand('copy');
    area.remove();
    if (!succeeded) {
      throw new Error('copy failed');
    }
  };

  // 結果領域ごと差し替わっても効くよう、結果領域（#search-results）で委譲する。
  const results = document.getElementById('search-results');
  if (results) {
    results.addEventListener('click', async (event) => {
      const button = event.target.closest('button[data-copy]');
      if (!button) {
        return;
      }
      const status = document.getElementById('copy-status');
      const original = button.textContent;
      let message;
      try {
        await copyText(button.dataset.copy);
        message = t('Js.Copy.Done');
      } catch (error) {
        message = t('Js.Copy.Failed');
      }
      button.textContent = message;
      if (status) {
        status.textContent = message;
      }
      window.setTimeout(() => {
        button.textContent = original;
      }, 1500);
    });
  }

  // ---- インクリメンタルサーチ（入力に合わせて結果だけを差し替える） ----
  // 有効/無効・開始文字数・待ち時間は管理者がシステム設定で決める（フォームの data-incremental-* に出力される）。
  // 全体の再読み込みはせず、フォーカスと入力中の内容を保ったまま、結果・保存用の条件だけを更新する。
  if (form && results && form.dataset.incremental === 'true') {
    const minChars = Number(form.dataset.incrementalMin);
    const delay = Number(form.dataset.incrementalDelay);
    const headerName = form.dataset.incrementalHeader;
    const saveButton = document.getElementById('save-open');
    let timer = null;
    let controller = null;
    let lastQuery = new URLSearchParams(new FormData(form)).toString();

    // 結果領域は、クリックの委譲先なので要素ごとではなく中身だけを入れ替える。
    const swap = (selector, doc, contentOnly = false) => {
      const current = document.querySelector(selector);
      const next = doc.querySelector(selector);
      if (!current || !next) {
        return;
      }
      if (contentOnly) {
        current.innerHTML = next.innerHTML;
      } else {
        current.replaceWith(next);
      }
    };

    const run = async () => {
      const params = new URLSearchParams(new FormData(form));
      const query = params.toString();
      if (query === lastQuery) {
        return;
      }
      lastQuery = query;
      if (controller) {
        controller.abort();
      }
      controller = new AbortController();
      const url = `${form.getAttribute('action') || window.location.pathname}?${query}`;
      try {
        const response = await fetch(url, {
          headers: { Accept: 'text/html', [headerName]: '1' },
          signal: controller.signal,
        });
        if (!response.ok) {
          return;
        }
        const doc = new DOMParser().parseFromString(await response.text(), 'text/html');
        swap('#search-results', doc, true);
        swap('[data-incremental-swap="query-fields"]', doc);
        swap('[data-incremental-swap="banner"]', doc);
        const nextSave = doc.getElementById('save-open') || doc.querySelector('#save-open');
        if (saveButton && nextSave) {
          saveButton.disabled = nextSave.disabled;
          saveButton.title = nextSave.title;
        }
        setupColumns('result-table', 'result-columns');
        window.history.replaceState(null, '', url);
      } catch (error) {
        // 中断（次の入力による）や通信失敗では何もしない。Enter / 検索ボタンでの通常の検索は使える。
      }
    };

    const schedule = (input) => {
      window.clearTimeout(timer);
      if (input.value.trim().length < minChars) {
        return;
      }
      timer = window.setTimeout(run, delay);
    };

    form.addEventListener('input', (event) => {
      const input = event.target;
      // キーワード欄だけが対象。日本語入力の変換中は確定まで待つ。
      if (!input.matches('[data-text]') || event.isComposing) {
        return;
      }
      schedule(input);
    });
    form.addEventListener('compositionend', (event) => {
      if (event.target.matches('[data-text]')) {
        schedule(event.target);
      }
    });
    // Enter / 検索ボタンでの通常の検索に切り替わるときは、待機中の自動検索を取り消す。
    form.addEventListener('submit', () => {
      window.clearTimeout(timer);
      if (controller) {
        controller.abort();
      }
    });
  }
})();

// ログイン画面: 標準 / ActiveDirectory のタブ切り替え。
(() => {
  const form = document.querySelector('[data-login-form]');
  const tabs = form ? [...form.querySelectorAll('[data-auth-tab]')] : [];
  if (tabs.length === 0) {
    return;
  }

  const source = form.querySelector('[data-auth-source]');
  const panels = [...form.querySelectorAll('[data-auth-panel]')];
  const cookieName = 'nowfound-login-auth';
  const storageKey = 'nowfound.login.authSource';
  const validSources = tabs.map((tab) => tab.dataset.authTab);

  const saveAuthSource = (auth) => {
    if (!validSources.includes(auth)) {
      return;
    }
    // 次回アクセス時にサーバー側で SSR に反映できるよう Cookie に保存する。
    try {
      const secure = window.location.protocol === 'https:' ? '; Secure' : '';
      document.cookie = `${cookieName}=${encodeURIComponent(auth)}; path=/; max-age=31536000; SameSite=Lax${secure}`;
    } catch (error) {
      // Cookie 保存不可な環境では無視する。
    }
    // localStorage にも保存する。
    try {
      window.localStorage.setItem(storageKey, auth);
    } catch (error) {
      // localStorage が使えない環境では無視する。
    }
  };

  const select = (auth, focus) => {
    if (!validSources.includes(auth)) {
      auth = validSources[0];
    }
    source.value = auth;
    tabs.forEach((tab) => {
      const selected = tab.dataset.authTab === auth;
      tab.setAttribute('aria-selected', String(selected));
      tab.tabIndex = selected ? 0 : -1;
      if (selected && focus) {
        tab.focus();
      }
    });
    panels.forEach((panel) => {
      const active = panel.dataset.authPanel === auth;
      panel.hidden = !active;
      // 隠れているタブの入力は、必須チェックと送信の対象から外す。
      panel.querySelectorAll('input').forEach((input) => {
        input.disabled = !active;
      });
    });
    const first = panels.find((panel) => !panel.hidden)?.querySelector('input');
    if (focus && first) {
      first.focus();
    }
  };

  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => {
      select(tab.dataset.authTab, false);
      saveAuthSource(tab.dataset.authTab);
    });
    // 矢印キーでタブを移動する（WAI-ARIA のタブの操作）。
    tab.addEventListener('keydown', (event) => {
      const step = { ArrowRight: 1, ArrowLeft: -1 }[event.key];
      if (step) {
        event.preventDefault();
        const nextAuth = tabs[(index + step + tabs.length) % tabs.length].dataset.authTab;
        select(nextAuth, true);
        saveAuthSource(nextAuth);
      }
    });
  });

  // 初期選択: サーバー側でレンダリングされた source.value を優先。
  // Cookie が無く localStorage にのみ前回の選択が残っていた場合はそこから復元する。
  let initial = source.value;
  if (!validSources.includes(initial) || initial === 'Local') {
    try {
      const hasCookie = document.cookie.split(';').some((item) => item.trim().startsWith(`${cookieName}=`));
      if (!hasCookie) {
        const saved = window.localStorage.getItem(storageKey);
        if (validSources.includes(saved)) {
          initial = saved;
        }
      }
    } catch (error) {
      // ignore
    }
  }

  select(initial, false);
  if (initial !== source.value) {
    saveAuthSource(initial);
  }
})();

// ユーザー追加: 認証方式（標準 / ActiveDirectory）で入力欄を切り替える。
(() => {
  const select = document.querySelector('[data-auth-select]');
  if (!select) {
    return;
  }

  const apply = () => {
    const directory = select.value === 'ActiveDirectory';
    const input = document.querySelector('[data-auth-input]');
    input.type = directory ? 'text' : 'email';
    document.querySelectorAll('[data-auth-only]').forEach((section) => {
      const hidden = section.dataset.authOnly === (directory ? 'local' : 'directory');
      section.hidden = hidden;
      // 隠れている方式の入力は、必須チェックと送信の対象から外す。
      section.querySelectorAll('input').forEach((field) => {
        field.disabled = hidden;
      });
    });
    document.querySelectorAll('[data-auth-note]').forEach((note) => {
      note.hidden = note.dataset.authNote !== (directory ? 'directory' : 'local');
    });
  };

  // 前回選んだ認証方式を覚えておく（入力エラーで再表示されたときは、入力した値を優先する）。
  const storageKey = 'nowfound.users.create.authSource';
  const options = [...select.options].map((option) => option.value);
  if (select.dataset.authRestore === 'true') {
    try {
      const saved = window.localStorage.getItem(storageKey);
      if (options.includes(saved)) {
        select.value = saved;
      }
    } catch (error) {
      // localStorage が使えない環境では、既定の認証方式のままにする。
    }
  }

  select.addEventListener('change', () => {
    apply();
    try {
      window.localStorage.setItem(storageKey, select.value);
    } catch (error) {
      // 保存できない環境では記憶しないだけにする。
    }
  });
  apply();
})();

// ActiveDirectory 設定: 接続の保護を切り替えたとき、ポートがそれまでの既定値のままなら新しい既定値に合わせる。
(() => {
  const security = document.querySelector('[data-ad-security]');
  const port = document.querySelector('[data-ad-port]');
  if (!security || !port) {
    return;
  }

  const defaults = new Set([...security.options].map((option) => option.dataset.port));
  security.addEventListener('change', () => {
    if (port.value === '' || defaults.has(port.value)) {
      port.value = security.selectedOptions[0].dataset.port;
    }
  });
})();

// 確認ボタン（ActiveDirectory の接続確認・ユーザーの存在確認）: フォームの一部の値を POST し、結果をその場に表示する。
(() => {
  const show = (result, message, success) => {
    result.hidden = false;
    result.className = success ? 'notice' : 'validation-summary';
    result.textContent = message;
  };

  document.querySelectorAll('[data-check-url]').forEach((button) => {
    const form = button.closest('form');
    const result = document.querySelector(button.dataset.checkResult);
    if (!form || !result) {
      return;
    }

    button.addEventListener('click', async () => {
      const data = new FormData();
      const token = form.querySelector('input[name="__RequestVerificationToken"]');
      if (token) {
        data.append(token.name, token.value);
      }
      button.dataset.checkFields.split(',').forEach((name) => {
        const field = form.elements[name];
        if (field) {
          data.append(name, field.value);
        }
      });

      button.disabled = true;
      show(result, button.dataset.checkRunning, true);
      try {
        const response = await fetch(button.dataset.checkUrl, {
          method: 'POST',
          body: data,
          headers: { Accept: 'application/json' },
        });
        if (!response.ok) {
          throw new Error(String(response.status));
        }
        const body = await response.json();
        show(result, body.message, body.success);
      } catch (error) {
        show(result, button.dataset.checkError, false);
      } finally {
        button.disabled = false;
      }
    });
  });
})();

// 検索対象フォルダ: パスを入力したら、表示名が未入力（または自動入力のまま）のときだけ、フォルダ名を表示名に入れる。
(() => {
  const pathInput = document.querySelector('[data-folder-path]');
  const nameInput = document.querySelector('[data-folder-name]');
  if (!pathInput || !nameInput) {
    return;
  }

  const folderName = (path) => path.trim().replace(/[\\/]+$/, '').split(/[\\/]/).pop();

  // 画面を開いたときに表示名が入っている（編集・入力エラーでの再表示）なら、利用者が決めた名前として上書きしない。
  let auto = nameInput.value === '';
  nameInput.addEventListener('input', () => {
    auto = nameInput.value === '';
  });
  pathInput.addEventListener('input', () => {
    if (auto) {
      nameInput.value = folderName(pathInput.value);
    }
  });
})();
