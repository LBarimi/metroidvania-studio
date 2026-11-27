// Shortcut guides use the editor language. Links to English guides keep their destination titles.
const languages = ['ko', 'ja', 'zh-hans', 'zh-hant-tw', 'ru'];
const labels = {
  'Docs': ['문서', 'ドキュメント', '文档', '文件', 'Документация'],
  'Documentation': ['가이드 문서', 'ドキュメント', '文档', '說明文件', 'Документация'],
  'Overview': ['개요', '概要', '概览', '總覽', 'Обзор'],
  'Skip to content': ['본문 바로가기', '本文へ移動', '跳至正文', '跳至本文', 'Перейти к содержимому'],
  'Search documentation': ['문서 검색', 'ドキュメントを検索', '搜索文档', '搜尋說明文件', 'Поиск по документации'],
  'Rooms, autotiling, Lua…': ['방, 자동 타일 배치, Lua…', '部屋、オートタイル、Lua…', '房间、自动图块、Lua…', '房間、自動圖塊、Lua…', 'Комнаты, автотайлинг, Lua…'],
  'On this page': ['이 페이지의 내용', 'このページの内容', '本页目录', '本頁目錄', 'На этой странице'],
  'Start here': ['시작하기', 'はじめに', '入门', '入門', 'Начало работы'],
  'Map editing': ['맵 편집', 'マップ編集', '地图编辑', '地圖編輯', 'Редактирование карты'],
  'Automation': ['자동화', '自動化', '自动化', '自動化', 'Автоматизация'],
  'Lua scripts': ['Lua 스크립트', 'Lua スクリプト', 'Lua 脚本', 'Lua 指令碼', 'Скрипты Lua'],
  'API reference': ['API 레퍼런스', 'API リファレンス', 'API 参考', 'API 參考', 'Справочник API'],
  'Reference & distribution': ['참고 자료와 배포', 'リファレンスと配布', '参考资料与分发', '參考資料與發佈', 'Справка и распространение'],
  'More guides': ['기타 가이드', 'その他のガイド', '更多指南', '更多指南', 'Другие руководства'],
  '{0} matching pages': ['검색 결과 {0}개', '検索結果 {0} 件', '找到 {0} 页', '找到 {0} 頁', 'Найдено страниц: {0}'],
  'No matching pages': ['검색 결과가 없습니다', '該当するページはありません', '没有找到匹配的页面', '找不到符合的頁面', 'Страницы не найдены'],
  'Documentation works offline.': ['오프라인에서도 문서를 볼 수 있습니다.', 'オフラインでも閲覧できます。', '可离线阅读文档。', '可離線閱讀說明文件。', 'Документация доступна без подключения к сети.'],
  'Offline guides are available through Help → Documentation in the studio.': ['편집기의 도움말 → 가이드 문서에서 오프라인으로 볼 수 있습니다.', 'エディターのヘルプ → ドキュメントからオフラインでも閲覧できます。', '在编辑器中打开帮助 → 文档即可离线阅读。', '在編輯器中開啟說明 → 說明文件即可離線閱讀。', 'В редакторе откройте Справка → Документация для чтения без подключения к сети.']
};

export function docsLabel(language, english) {
  return labels[english]?.[languages.indexOf(language)] ?? english;
}
