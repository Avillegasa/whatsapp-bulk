import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule],
  template: `
    <div class="login">
      <aside class="login-marca" aria-hidden="true">
        <span class="marca-logo">
          <svg class="icono" viewBox="0 0 24 24"><path d="M5 5h14v10H10l-4 3.5V15H5zM9 9h6M9 11.5h4" /></svg>
        </span>
        <p class="login-lema">Promociones por WhatsApp, solo a clientes que aceptaron recibirlas.</p>
        <ul class="login-puntos">
          <li>Cada campaña la aprueba otra persona</li>
          <li>Las bajas se respetan al encolar y al enviar</li>
          <li>Monitor del despacho en vivo</li>
        </ul>
      </aside>
      <section class="login-tarjeta">
        <h1>WhatsApp Bulk</h1>
        <p class="sub">Inicia sesión con tu usuario de la intranet</p>
        <form [formGroup]="form" (ngSubmit)="entrar()" novalidate>
          <label class="campo" for="usuario">Usuario</label>
          <input id="usuario" formControlName="usuario" autocomplete="username" />
          <label class="campo" for="clave">Clave</label>
          <input id="clave" type="password" formControlName="clave" autocomplete="current-password" />
          @if (error()) {
            <p class="aviso error" role="alert">{{ error() }}</p>
          }
          <button type="submit" [disabled]="form.invalid || enviando()">
            {{ enviando() ? 'Entrando…' : 'Entrar' }}
          </button>
        </form>
      </section>
    </div>
  `,
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly form = inject(NonNullableFormBuilder).group({
    usuario: ['', Validators.required],
    clave: ['', Validators.required],
  });
  protected readonly error = signal<string | null>(null);
  protected readonly enviando = signal(false);

  protected async entrar(): Promise<void> {
    if (this.form.invalid) {
      return;
    }
    this.enviando.set(true);
    this.error.set(null);
    const { usuario, clave } = this.form.getRawValue();
    const fallo = await this.auth.login(usuario, clave);
    this.enviando.set(false);
    if (fallo) {
      this.error.set(fallo.mensaje);
      this.form.controls.clave.reset();
      return;
    }
    const volver = this.route.snapshot.queryParamMap.get('volver');
    // Solo rutas internas (evita redirigir a otro sitio con ?volver=https://…).
    await this.router.navigateByUrl(volver?.startsWith('/') && !volver.startsWith('//') ? volver : '/');
  }
}
