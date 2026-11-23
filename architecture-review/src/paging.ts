export interface PageWindow<T> {
  items: T[];
  page: number;
  pages: number;
  start: number;
  end: number;
}

export function pageWindow<T>(items: readonly T[], requestedPage: number, pageSize: number): PageWindow<T> {
  const size = Math.max(1, Math.floor(pageSize));
  const pages = Math.max(1, Math.ceil(items.length / size));
  const page = Math.max(1, Math.min(pages, Math.floor(requestedPage) || 1));
  const offset = (page - 1) * size;
  const visible = items.slice(offset, offset + size);
  return {
    items: visible,
    page,
    pages,
    start: visible.length ? offset + 1 : 0,
    end: offset + visible.length,
  };
}
