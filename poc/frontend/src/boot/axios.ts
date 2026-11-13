import { defineBoot } from '#q-app';
import { http } from 'src/services/api/http';

export default defineBoot(({ app }) => {
  app.config.globalProperties.$http = http;
});
