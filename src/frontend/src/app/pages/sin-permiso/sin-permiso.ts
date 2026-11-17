import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-sin-permiso',
  imports: [RouterLink],
  template: `
    <h1>Sin permiso</h1>
    <p>Tu rol no permite ver esta pantalla. Si la necesitas, pídeselo a un Admin.</p>
    <a routerLink="/">Volver al inicio</a>
  `,
})
export class SinPermiso {}
