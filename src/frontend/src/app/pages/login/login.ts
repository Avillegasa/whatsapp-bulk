import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule],
  template: `
    <section class="tarjeta">
      <h1>WhatsApp Bulk</h1>
      <p class="sub">Inicia sesión con tu usuario de la intranet</p>
      <form [formGroup]="form" (ngSubmit)="entrar()">
        <label for="usuario">Usuario</label>
        <input id="usuario" formControlName="usuario" autocomplete="username" />
        <label for="clave">Clave</label>
        <input id="clave" type="password" formControlName="clave" autocomplete="current-password" />
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <button type="submit" [disabled]="form.invalid || enviando()">
          {{ enviando() ? 'Entrando…' : 'Entrar' }}
        </button>
      </form>
    </section>
  `,
  styles: `
    :host { display: grid; place-items: center; min-height: 100vh; background: #f0f2f5; }
    .tarjeta { background: #fff; padding: 2rem; border-radius: 8px; width: min(360px, 90vw); box-shadow: 0 1px 4px rgb(0 0 0 / 15%); }
    h1 { margin: 0; color: #075e54; font-size: 1.5rem; }
    .sub { color: #555; margin: 0.25rem 0 1.5rem; }
    form { display: grid; gap: 0.5rem; }
    input { padding: 0.5rem; border: 1px solid #bbb; border-radius: 4px; font-size: 1rem; }
    button { margin-top: 1rem; padding: 0.6rem; background: #075e54; color: #fff; border: 0; border-radius: 4px; font-size: 1rem; cursor: pointer; }
    button:disabled { opacity: 0.6; cursor: default; }
    .error { color: #b3261e; margin: 0.5rem 0 0; }
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
