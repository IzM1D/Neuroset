import { useEffect, useRef, useState } from 'react';
import { ArrowDown, ArrowUp, BookOpen, Check, ChevronDown, Code2, Download, Lightbulb, Menu, MessageCircle, Moon, PanelLeftClose, Pencil, PenLine, Plus, Search, Settings2, Sparkles, Square, Sun, Trash2, X } from 'lucide-react';
import { completeChat, friendlyError } from './api';
import MessageView from './MessageView';
import SettingsDialog from './SettingsDialog';
import { loadChats, loadSettings, saveChats, saveSettings } from './storage';
import { createChat, type Chat, type Message, type Settings } from './types';

const suggestions = [
  { icon: PenLine, title: 'Написать текст', description: 'Найти нужные слова', prompt: 'Помоги написать текст. Сначала уточни тему, аудиторию и желаемый стиль.' },
  { icon: Lightbulb, title: 'Придумать идею', description: 'Взглянуть по-новому', prompt: 'Давай устроим мозговой штурм. Спроси, для какой задачи мне нужны идеи.' },
  { icon: Code2, title: 'Разобраться в коде', description: 'От вопроса к решению', prompt: 'Помоги разобраться в коде. Спроси, какой язык я использую и что хочу сделать.' },
  { icon: BookOpen, title: 'Узнать новое', description: 'Объяснить сложное просто', prompt: 'Хочу разобраться в новой теме. Спроси, что мне интересно и что я уже знаю.' },
];

export default function App() {
  const [settings, setSettings] = useState(loadSettings);
  const [chats, setChats] = useState(loadChats);
  const [activeId, setActiveId] = useState(() => chats[0].id);
  const [sidebarOpen, setSidebarOpen] = useState(() => window.innerWidth > 760);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [search, setSearch] = useState('');
  const [busy, setBusy] = useState<{ chatId: string; messageId: string } | null>(null);
  const [toast, setToast] = useState('');
  const [storageError, setStorageError] = useState('');
  const [editingId, setEditingId] = useState<string | null>(null);
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameText, setRenameText] = useState('');
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [showScroll, setShowScroll] = useState(false);
  const controller = useRef<AbortController | null>(null);
  const textarea = useRef<HTMLTextAreaElement>(null);
  const searchInput = useRef<HTMLInputElement>(null);
  const scrollArea = useRef<HTMLDivElement>(null);
  const follow = useRef(true);
  const deleteDialog = useRef<HTMLDialogElement>(null);
  const activeChat = chats.find(chat => chat.id === activeId) || chats[0];
  const isEmpty = activeChat.messages.length === 0;

  useEffect(() => {
    const timer = window.setTimeout(() => {
      try { saveChats(chats); setStorageError(''); }
      catch { setStorageError('История не сохранена: хранилище браузера недоступно или заполнено. Скачайте важные чаты.'); }
    }, 250);
    const flush = () => { try { saveChats(chats); } catch { /* The UI reports storage failures. */ } };
    window.addEventListener('pagehide', flush);
    return () => { window.clearTimeout(timer); window.removeEventListener('pagehide', flush); };
  }, [chats]);

  useEffect(() => { document.documentElement.dataset.theme = settings.theme; }, [settings.theme]);
  useEffect(() => { document.title = isEmpty ? 'Neuroset — чат с нейросетью' : `${activeChat.title} — Neuroset`; }, [activeChat.title, isEmpty]);
  useEffect(() => {
    if (!toast) return;
    const timer = window.setTimeout(() => setToast(''), 4000);
    return () => window.clearTimeout(timer);
  }, [toast]);
  useEffect(() => {
    if (textarea.current) {
      textarea.current.style.height = 'auto';
      textarea.current.style.height = `${Math.min(textarea.current.scrollHeight, 200)}px`;
    }
  }, [activeChat.draft, activeId]);
  useEffect(() => {
    if (follow.current && scrollArea.current) scrollArea.current.scrollTop = scrollArea.current.scrollHeight;
  }, [activeChat.messages, activeId]);
  useEffect(() => { if (deleteId) deleteDialog.current?.showModal(); }, [deleteId]);
  useEffect(() => () => controller.current?.abort(), []);

  function updateChat(id: string, update: (chat: Chat) => Chat) {
    setChats(previous => previous.map(chat => chat.id === id ? update(chat) : chat));
  }

  function chooseChat(id: string) {
    setActiveId(id);
    setEditingId(null);
    follow.current = true;
    setShowScroll(false);
    if (window.innerWidth <= 760) setSidebarOpen(false);
  }

  function newChat() {
    const empty = chats.find(chat => !chat.messages.length && !chat.draft);
    if (empty) chooseChat(empty.id);
    else {
      const chat = createChat();
      setChats(previous => [chat, ...previous]);
      chooseChat(chat.id);
    }
    setSearch('');
    textarea.current?.focus();
  }

  function persistSettings(next: Settings) {
    setSettings(next);
    try { saveSettings(next); }
    catch { setToast('Настройки действуют, но браузер не разрешил их сохранить.'); }
  }

  async function generate(history: Message[]) {
    if (controller.current) return;
    const chatId = activeChat.id;
    const response: Message = { id: crypto.randomUUID(), role: 'assistant', content: '' };
    const abort = new AbortController();
    controller.current = abort;
    setBusy({ chatId, messageId: response.id });
    follow.current = true;
    updateChat(chatId, chat => ({ ...chat, messages: [...history, response], draft: '', updatedAt: Date.now(),
      title: chat.messages.length === 0 ? (history[0]?.content.slice(0, 55) || 'Новый чат') : chat.title }));
    let content = '';
    let pendingFrame = 0;
    const updateResponse = (patch: Partial<Message>) => updateChat(chatId, chat => ({ ...chat,
      messages: chat.messages.map(message => message.id === response.id ? { ...message, ...patch } : message),
    }));
    try {
      await completeChat(settings, history, AbortSignal.any([abort.signal, AbortSignal.timeout(300_000)]), text => {
        content += text;
        if (!pendingFrame) pendingFrame = requestAnimationFrame(() => { pendingFrame = 0; updateResponse({ content }); });
      });
      updateResponse({ content });
    } catch (cause) {
      updateResponse({ content, ...(abort.signal.aborted ? { interrupted: true } : {
        error: cause instanceof DOMException && cause.name === 'TimeoutError' ? 'Время ожидания ответа истекло (5 минут). Повторите запрос.' : friendlyError(cause),
      }) });
    } finally {
      cancelAnimationFrame(pendingFrame);
      controller.current = null;
      setBusy(null);
      textarea.current?.focus();
    }
  }

  function checkConnection() {
    if (settings.model.trim() && settings.baseUrl.trim()) return true;
    setSettingsOpen(true);
    return false;
  }

  function send() {
    const content = activeChat.draft.trim();
    if (!content || busy || !checkConnection()) return;
    const editIndex = activeChat.messages.findIndex(message => message.id === editingId);
    const history = editIndex >= 0 ? activeChat.messages.slice(0, editIndex) : activeChat.messages;
    setEditingId(null);
    void generate([...history, { id: crypto.randomUUID(), role: 'user', content }]);
  }

  function retry(index: number) {
    if (busy || !checkConnection()) return;
    void generate(activeChat.messages.slice(0, index));
  }

  function exportChat() {
    const markdown = `# ${activeChat.title}\n\n` + activeChat.messages.map(message => `## ${message.role === 'user' ? 'Вы' : 'Neuroset'}\n\n${message.content}${message.error ? `\n\nОшибка: ${message.error}` : ''}`).join('\n\n---\n\n');
    const url = URL.createObjectURL(new Blob([markdown], { type: 'text/markdown;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = `${activeChat.title.replace(/[<>:"/\\|?*\x00-\x1F]/g, '').slice(0, 60) || 'Neuroset'}.md`;
    link.click();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  const filtered = [...chats].sort((a, b) => b.updatedAt - a.updatedAt).filter(chat =>
    `${chat.title} ${chat.messages.map(message => message.content).join(' ')}`.toLocaleLowerCase().includes(search.toLocaleLowerCase()));

  return <div className={`app-shell ${sidebarOpen ? 'sidebar-open' : ''}`}>
    <a className="skip-link" href="#message-input">Перейти к сообщению</a>
    {sidebarOpen && <button className="sidebar-backdrop" aria-label="Закрыть меню" onClick={() => setSidebarOpen(false)} />}
    <aside className="sidebar" aria-label="История чатов" inert={!sidebarOpen}>
      <div className="sidebar-heading"><a className="brand" href="#" onClick={event => { event.preventDefault(); newChat(); }}><span className="brand-mark">N</span>neuroset<span className="brand-dot">.</span></a><button className="icon-button" aria-label="Скрыть боковую панель" title="Скрыть боковую панель" onClick={() => setSidebarOpen(false)}><PanelLeftClose size={19} /></button></div>
      <button className="new-chat" onClick={newChat}><Plus size={19} />Новый чат<span className="new-chat-symbol"><PenLine size={15} /></span></button>
      <div className="search-field"><Search size={16} /><input ref={searchInput} aria-label="Поиск по чатам" placeholder="Поиск по чатам" value={search} onChange={event => setSearch(event.target.value)} />{search && <button className="icon-button" aria-label="Очистить поиск" onClick={() => setSearch('')}><X size={14} /></button>}</div>
      <div className="history-label">Ваши чаты<span>{chats.filter(chat => chat.messages.length).length || ''}</span></div>
      <nav className="chat-list" aria-label="Чаты">
        {!filtered.length && <p className="history-empty">Ничего не найдено</p>}
        {filtered.map(chat => <div key={chat.id} className={`chat-row ${chat.id === activeChat.id ? 'active' : ''}`}>
          {renamingId === chat.id ? <form className="rename-form" onSubmit={event => {
            event.preventDefault();
            if (renameText.trim()) updateChat(chat.id, current => ({ ...current, title: renameText.trim() }));
            setRenamingId(null);
          }}><input autoFocus aria-label="Название чата" value={renameText} maxLength={100} onChange={event => setRenameText(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') setRenamingId(null); }} /><button className="icon-button" aria-label="Сохранить название"><Check size={15} /></button></form> : <>
            <button className="chat-select" aria-current={chat.id === activeChat.id ? 'page' : undefined} title={chat.title} onClick={() => chooseChat(chat.id)}><MessageCircle size={15} /><span>{chat.title}</span>{busy?.chatId === chat.id && <span className="generating-dot" />}</button>
            <div className="chat-row-actions"><button className="icon-button" aria-label={`Переименовать: ${chat.title}`} title="Переименовать" onClick={() => { setRenamingId(chat.id); setRenameText(chat.title); }}><Pencil size={13} /></button><button className="icon-button" aria-label={`Удалить: ${chat.title}`} title="Удалить чат" disabled={busy?.chatId === chat.id} onClick={() => setDeleteId(chat.id)}><Trash2 size={13} /></button></div>
          </>}
        </div>)}
      </nav>
      <div className="sidebar-footer">
        <button className="settings-link" onClick={() => setSettingsOpen(true)}><Settings2 size={18} /><span>Настройки</span><span className={`connection-dot ${settings.model ? 'configured' : ''}`} /></button>
        <div className="local-note"><span className="local-dot" />История хранится в вашем браузере</div>
        <div className="profile"><div className="profile-avatar">В</div><div><strong>Ваше пространство</strong><span>Neuroset Web</span></div><button className="icon-button" aria-label={settings.theme === 'dark' ? 'Светлая тема' : 'Тёмная тема'} title="Переключить тему" onClick={() => persistSettings({ ...settings, theme: settings.theme === 'dark' ? 'light' : 'dark' })}>{settings.theme === 'dark' ? <Sun size={18} /> : <Moon size={18} />}</button></div>
      </div>
    </aside>

    <main className="main-panel">
      <header className="topbar"><div className="topbar-left">{!sidebarOpen && <button className="icon-button" title="Открыть меню" aria-label="Открыть меню" onClick={() => setSidebarOpen(true)}><Menu size={21} /></button>}<button className="model-selector" onClick={() => setSettingsOpen(true)}><span>{settings.model || 'Neuroset'}</span><ChevronDown size={16} /></button><span className="topbar-tag">{settings.model ? 'Чат' : 'Ваш AI-помощник'}</span></div>
        <div className="topbar-actions">{!isEmpty && <button className="icon-button" title="Скачать чат" aria-label="Скачать чат" onClick={exportChat}><Download size={18} /></button>}<button className="icon-button" title="Новый чат" aria-label="Новый чат" onClick={newChat}><PenLine size={19} /></button></div>
      </header>
      {storageError && <div className="storage-error" role="alert">{storageError}</div>}
      <div className={`chat-stage ${isEmpty ? 'empty-stage' : ''}`}>
        {isEmpty ? <section className="welcome">
          <div className="welcome-badge"><Sparkles size={16} /><span>Место для ваших идей</span></div>
          <h1>С чего начнём?</h1>
          <p>Спросите, придумайте, разберитесь.<br className="mobile-break" /> Я помогу сделать следующий шаг.</p>
        </section> : <div className="conversation-scroll" ref={scrollArea} onScroll={() => {
          const element = scrollArea.current!;
          follow.current = element.scrollHeight - element.scrollTop - element.clientHeight < 90;
          setShowScroll(!follow.current);
        }}><div className="messages">
          {activeChat.messages.map((message, index) => <MessageView key={message.id} message={message} busy={!!busy} generating={busy?.messageId === message.id} canRetry={message.role === 'assistant' && index === activeChat.messages.length - 1} onRetry={() => retry(index)} onEdit={() => {
            setEditingId(message.id);
            updateChat(activeChat.id, chat => ({ ...chat, draft: message.content }));
            textarea.current?.focus();
          }} onCopyError={() => setToast('Не удалось скопировать. Выделите текст и скопируйте вручную.')} />)}
        </div></div>}

        <div className="composer-area">
          {showScroll && !isEmpty && <button className="scroll-bottom icon-button" title="К последнему сообщению" aria-label="К последнему сообщению" onClick={() => { follow.current = true; scrollArea.current?.scrollTo({ top: scrollArea.current.scrollHeight, behavior: 'smooth' }); }}><ArrowDown size={18} /></button>}
          {editingId && <div className="edit-notice"><span>Редактирование · последующие ответы будут заменены</span><button className="icon-button" aria-label="Отменить редактирование" onClick={() => { setEditingId(null); updateChat(activeChat.id, chat => ({ ...chat, draft: '' })); }}><X size={16} /></button></div>}
          <form className="composer" onSubmit={event => { event.preventDefault(); send(); }}>
            <textarea ref={textarea} id="message-input" aria-label="Сообщение" rows={1} placeholder="Спросите о чём угодно…" value={activeChat.draft} onChange={event => updateChat(activeChat.id, chat => ({ ...chat, draft: event.target.value }))} onKeyDown={event => {
              if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing && !window.matchMedia('(pointer: coarse)').matches) { event.preventDefault(); send(); }
            }} />
            <div className="composer-bottom"><button className="composer-model" type="button" onClick={() => setSettingsOpen(true)}><span className={`connection-dot ${settings.model ? 'configured' : ''}`} /><span>{settings.model || 'Подключить модель'}</span><ChevronDown size={13} /></button>
              {busy ? <button className="send-button stop-button" type="button" title="Остановить ответ" aria-label="Остановить ответ" onClick={() => controller.current?.abort()}><Square size={15} fill="currentColor" /></button> : <button className="send-button" type="submit" title="Отправить сообщение" aria-label="Отправить сообщение" disabled={!activeChat.draft.trim()}><ArrowUp size={20} strokeWidth={2.4} /></button>}
            </div>
          </form>
          {isEmpty && <div className="suggestions">{suggestions.map(({ icon: Icon, title, description, prompt }) => <button key={title} onClick={() => { updateChat(activeChat.id, chat => ({ ...chat, draft: prompt })); textarea.current?.focus(); }}><Icon size={19} /><strong>{title}</strong><span>{description}</span></button>)}</div>}
          <p className="composer-hint">{isEmpty ? 'Ваши вопросы — начало чего-то интересного.' : 'Neuroset может ошибаться. Проверяйте важную информацию.'}<span className="keyboard-hint">{isEmpty ? '' : 'Shift + Enter — новая строка'}</span></p>
        </div>
      </div>
      {isEmpty && <footer className="welcome-footer"><span className="footer-mark">N</span>Меньше рутины. Больше возможностей.</footer>}
    </main>

    {settingsOpen && <SettingsDialog settings={settings} onSave={next => { persistSettings(next); setSettingsOpen(false); textarea.current?.focus(); }} onClose={() => setSettingsOpen(false)} />}
    {deleteId && <dialog className="confirm-dialog" ref={deleteDialog} onCancel={() => setDeleteId(null)}><h2>Удалить чат?</h2><p>«{chats.find(chat => chat.id === deleteId)?.title}» будет удалён из этого браузера.</p><div className="dialog-footer"><button className="secondary-button" onClick={() => setDeleteId(null)}>Отмена</button><button className="danger-button" onClick={() => {
      const remaining = chats.filter(chat => chat.id !== deleteId);
      if (!remaining.length) remaining.push(createChat());
      setChats(remaining);
      if (activeChat.id === deleteId) chooseChat(remaining[0].id);
      setDeleteId(null);
    }}>Удалить</button></div></dialog>}
    {toast && <div className="toast" role="status">{toast}</div>}
  </div>;
}
