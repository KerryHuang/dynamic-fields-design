import { defineBoot } from '#q-app';
import { createPinia } from 'pinia';

export default defineBoot(({ app }) => {
  app.use(createPinia());
});
