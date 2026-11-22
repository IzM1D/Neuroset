import { useEffect, useState } from 'react';
import { File as FileIcon, X } from 'lucide-react';
import { loadImage } from './assets';
import { isPreviewImage } from './attachments';
import type { FileAttachment } from './types';

function AttachmentPreview({ attachment, onRemove, disabled }: {
  attachment: FileAttachment; onRemove?: (id: string) => void; disabled?: boolean;
}) {
  const [url, setUrl] = useState('');
  const [error, setError] = useState('');
  const preview = isPreviewImage(attachment.mediaType);
  useEffect(() => {
    let active = true;
    let objectUrl = '';
    loadImage(attachment.id).then(blob => {
      if (!active) return;
      if (!blob) { setError('Файл отсутствует в хранилище'); return; }
      objectUrl = URL.createObjectURL(preview ? blob : blob.slice(0, blob.size, 'application/octet-stream'));
      setUrl(objectUrl);
    }).catch(() => { if (active) setError('Не удалось загрузить файл'); });
    return () => { active = false; URL.revokeObjectURL(objectUrl); };
  }, [attachment.id, preview]);
  return <figure className="attachment-preview">
    {!url ? <span className="attachment-placeholder">{error || 'Загрузка…'}</span>
      : preview ? <a href={url} target="_blank" rel="noopener noreferrer" title={`Открыть ${attachment.name}`}><img src={url} alt={attachment.name} /></a>
      : <a className="file-download" href={url} download={attachment.name} aria-label={`Скачать ${attachment.name}`} title={`Скачать ${attachment.name}`}><FileIcon aria-hidden size={28} /></a>}
    <figcaption title={attachment.name}>{attachment.name}</figcaption>
    {onRemove && <button type="button" className="icon-button attachment-remove" aria-label={`Убрать ${attachment.name}`} disabled={disabled} onClick={() => onRemove(attachment.id)}><X size={14} /></button>}
  </figure>;
}

export default function AttachmentList({ attachments, onRemove, disabled }: {
  attachments: FileAttachment[]; onRemove?: (id: string) => void; disabled?: boolean;
}) {
  return <div className="attachments" aria-label={onRemove ? 'Прикреплённые файлы' : 'Вложения сообщения'}>
    {attachments.map(attachment => <AttachmentPreview key={attachment.id} attachment={attachment} onRemove={onRemove} disabled={disabled} />)}
  </div>;
}
