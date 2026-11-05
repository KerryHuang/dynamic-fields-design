import { defineBoot } from '@quasar/app-vite/wrappers';
import { http } from 'src/services/api/http';

export default defineBoot(({ app }) => {
  app.config.globalProperties.$http = http;
});
