import { useEffect, useState } from 'react';
import { X } from 'lucide-react';
import { loadImage } from './assets';
import type { ImageAttachment } from './types';

function AttachmentPreview({ attachment, onRemove, disabled }: {
  attachment: ImageAttachment; onRemove?: (id: string) => void; disabled?: boolean;
}) {
  const [url, setUrl] = useState('');
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    let objectUrl = '';
    loadImage(attachment.id).then(blob => {
      if (!active) return;
      if (!blob) { setError('Изображение отсутствует в хранилище'); return; }
      objectUrl = URL.createObjectURL(blob);
      setUrl(objectUrl);
    }).catch(() => { if (active) setError('Не удалось загрузить изображение'); });
    return () => { active = false; URL.revokeObjectURL(objectUrl); };
  }, [attachment.id]);
  return <figure className="attachment-preview">
    {url ? <a href={url} target="_blank" rel="noopener noreferrer" title={`Открыть ${attachment.name}`}><img src={url} alt={attachment.name} /></a>
      : <span className="attachment-placeholder">{error || 'Загрузка…'}</span>}
    <figcaption title={attachment.name}>{attachment.name}</figcaption>
    {onRemove && <button type="button" className="icon-button attachment-remove" aria-label={`Убрать ${attachment.name}`} disabled={disabled} onClick={() => onRemove(attachment.id)}><X size={14} /></button>}
  </figure>;
}

export default function AttachmentList({ attachments, onRemove, disabled }: {
  attachments: ImageAttachment[]; onRemove?: (id: string) => void; disabled?: boolean;
}) {
  return <div className="attachments" aria-label={onRemove ? 'Прикреплённые изображения' : 'Изображения сообщения'}>
    {attachments.map(attachment => <AttachmentPreview key={attachment.id} attachment={attachment} onRemove={onRemove} disabled={disabled} />)}
  </div>;
}
