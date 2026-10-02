import { useEffect, useRef, useState } from 'react';
import { Check, Eye, EyeOff, LoaderCircle, RefreshCw, SlidersHorizontal, X } from 'lucide-react';
import { discoverModels, endpoint, friendlyError } from './api';
import { isImageModel, selectImageModel } from './images';
import type { Settings } from './types';

interface Props {
  settings: Settings;
  onSave: (settings: Settings) => void;
  onClose: () => void;
}

export default function SettingsDialog({ settings, onSave, onClose }: Props) {
  const dialog = useRef<HTMLDialogElement>(null);
  const request = useRef<AbortController | null>(null);
  const [draft, setDraft] = useState(settings);
  const [visibleKey, setVisibleKey] = useState(false);
  const [models, setModels] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [modelStatus, setModelStatus] = useState('');
  const set = <K extends keyof Settings>(key: K, value: Settings[K]) => setDraft(previous => ({ ...previous, [key]: value }));

  useEffect(() => {
    dialog.current?.showModal();
    return () => { request.current?.abort(); };
  }, []);

  async function loadModels() {
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    setLoading(true);
    setError('');
    setModelStatus('');
    try {
      const result = await discoverModels(draft, AbortSignal.any([controller.signal, AbortSignal.timeout(30_000)]));
      setModels(result);
      if (!draft.imageModel) set('imageModel', selectImageModel(result));
      setModelStatus(result.length ? `Доступно моделей: ${result.length}` : 'Список пуст. Введите модель вручную.');
    } catch (cause) {
      if (!controller.signal.aborted) setError(friendlyError(cause));
    } finally { if (!controller.signal.aborted) setLoading(false); }
  }

  return <dialog ref={dialog} className="settings-dialog" onCancel={onClose} onClick={event => { if (event.target === event.currentTarget) onClose(); }}>
    <form onSubmit={event => {
      event.preventDefault();
      try {
        endpoint(draft.baseUrl);
        if (!draft.model.trim()) throw new Error('Введите название модели.');
        if (draft.imageModel.trim() && !isImageModel(draft.imageModel.trim())) throw new Error('Для изображений укажите GPT Image модель, например gpt-image-2.');
        onSave({ ...draft, baseUrl: draft.baseUrl.trim(), model: draft.model.trim(), apiKey: draft.apiKey.trim(), imageModel: draft.imageModel.trim(), searchKey: draft.searchKey.trim() });
      } catch (cause) { setError(friendlyError(cause)); }
    }}>
      <div className="dialog-heading">
        <div className="heading-icon"><SlidersHorizontal size={20} /></div>
        <div><h2>Настройки</h2><p>Ваша модель. Ваше пространство.</p></div>
        <button type="button" className="icon-button close-dialog" title="Закрыть настройки" aria-label="Закрыть настройки" onClick={onClose}><X size={20} /></button>
      </div>
      <div className="settings-body">
        <h3>Подключение к модели</h3>
        <label className="field">Адрес API
          <input type="url" required value={draft.baseUrl} onChange={event => set('baseUrl', event.target.value)} placeholder="https://api.openai.com/v1" spellCheck={false} />
          <span className="field-hint">OpenAI или любой совместимый провайдер. Можно указать полный URL /chat/completions.</span>
        </label>
        <label className="field">API-ключ
          <span className="password-field"><input type={visibleKey ? 'text' : 'password'} value={draft.apiKey} onChange={event => set('apiKey', event.target.value)} placeholder="sk-…" autoComplete="off" spellCheck={false} />
            <button className="icon-button" type="button" aria-label={visibleKey ? 'Скрыть ключ' : 'Показать ключ'} onClick={() => setVisibleKey(!visibleKey)}>{visibleKey ? <EyeOff size={17} /> : <Eye size={17} />}</button>
          </span>
        </label>
        <label className="checkbox-row"><input type="checkbox" checked={draft.rememberKey} onChange={event => set('rememberKey', event.target.checked)} />Запомнить ключ в этом браузере</label>
        <p className="field-hint key-hint">{draft.rememberKey ? 'Ключ хранится локально в браузере без шифрования.' : 'Ключ хранится только до закрытия вкладки.'} Запросы отправляются напрямую вашему API.</p>
        <label className="field">Модель
          <div className="model-input"><input aria-label="Модель" list="available-models" required value={draft.model} onChange={event => set('model', event.target.value)} placeholder="Например, gpt-4.1-mini" spellCheck={false} />
            <button className="secondary-button" type="button" onClick={loadModels} disabled={loading}>{loading ? <LoaderCircle className="spin" size={16} /> : <RefreshCw size={16} />}<span>Загрузить</span></button>
          </div>
          <datalist id="available-models">{models.map(model => <option key={model} value={model} />)}</datalist>
          <span className="field-hint">Введите название вручную или загрузите список из API.</span>
        </label>
        {modelStatus && <p className="model-status" role="status"><Check size={15} />{modelStatus}</p>}
        <details className="advanced-settings tool-settings" open>
          <summary>Инструменты</summary>
          <p className="field-hint">Для инструментов разговорная модель должна поддерживать function calling.</p>
          <label className="checkbox-row"><input type="checkbox" checked={draft.webEnabled} onChange={event => set('webEnabled', event.target.checked)} />Поиск в интернете и чтение ссылок</label>
          <label className="field">Ключ поиска Tavily<input aria-label="Ключ поиска Tavily" type="password" value={draft.searchKey} onChange={event => set('searchKey', event.target.value)} placeholder="tvly-…" autoComplete="off" />
            <span className="field-hint">Получить ключ: <a href="https://app.tavily.com" target="_blank" rel="noopener noreferrer">app.tavily.com</a>. Поиск и чтение страниц идут через Tavily. Без ключа ссылки читаются через Jina Reader. Запрос или URL передаётся выбранному сервису.</span>
          </label>
          <label className="checkbox-row"><input type="checkbox" checked={draft.pythonEnabled} onChange={event => set('pythonEnabled', event.target.checked)} />Python: расчёты, данные и графики</label>
          <p className="field-hint">Python загружается при первом запуске. Каждый расчёт выполняется отдельно в браузере; доступны NumPy, pandas, SymPy и matplotlib.</p>
          <label className="field">Модель изображений<input aria-label="Модель изображений" list="image-models" value={draft.imageModel} onChange={event => set('imageModel', event.target.value)} placeholder="Автоматически · например gpt-image-2" />
            <datalist id="image-models">{models.filter(isImageModel).map(model => <option key={model} value={model} />)}</datalist>
            <span className="field-hint">Тот же API и ключ, метод /images/generations. Модель определяется автоматически из списка; при необходимости укажите её вручную. Кнопка изображения включает генерацию для одного сообщения.</span>
          </label>
          <p className="field-hint">Ключ поиска сохраняется так же, как API-ключ: {draft.rememberKey ? 'в этом браузере' : 'до закрытия вкладки'}.</p>
        </details>
        <details className="advanced-settings">
          <summary>Параметры генерации</summary>
          <label className="field">Системная инструкция<textarea value={draft.systemPrompt} onChange={event => set('systemPrompt', event.target.value)} rows={4} placeholder="Как модель должна отвечать?" /></label>
          <div className="field-grid">
            <label className="field">Температура<input type="number" min="0" max="2" step="0.1" value={draft.temperature} onChange={event => set('temperature', event.target.value)} placeholder="По умолчанию" /></label>
            <label className="field">Максимум токенов<input type="number" min="1" max="1000000" step="1" value={draft.maxTokens} onChange={event => set('maxTokens', event.target.value)} placeholder="По умолчанию" /></label>
          </div>
          <p className="field-hint">Пустые поля используют параметры провайдера.</p>
          <label className="checkbox-row"><input type="checkbox" checked={draft.stream} onChange={event => set('stream', event.target.checked)} />Показывать ответ по мере генерации</label>
        </details>
        <label className="theme-field">Оформление<select value={draft.theme} onChange={event => set('theme', event.target.value as Settings['theme'])}><option value="dark">Тёмное</option><option value="light">Светлое</option></select></label>
        {error && <p className="error-notice" role="alert">{error}</p>}
      </div>
      <div className="dialog-footer"><button type="button" className="secondary-button" onClick={onClose}>Отмена</button><button className="primary-button" type="submit">Сохранить настройки</button></div>
    </form>
  </dialog>;
}
