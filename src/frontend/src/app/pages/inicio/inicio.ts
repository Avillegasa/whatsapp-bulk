import { Component, inject } from '@angular/core';
import { AuthService } from '../../core/auth.service';

/** Inicio provisional: el tablero llega en F1.8. */
@Component({
  selector: 'app-inicio',
  template: `
    <h1>Hola, {{ auth.usuario() }}</h1>
    <p>Tus roles: {{ auth.roles().join(', ') }}.</p>
    <p>Las pantallas de contactos, plantillas y campañas se irán sumando al menú en la Fase 1.</p>
  `,
})
export class Inicio {
  protected readonly auth = inject(AuthService);
}
