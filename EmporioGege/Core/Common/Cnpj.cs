namespace EmporioGege.Core.Common
{
    public static class Cnpj
    {
        private static readonly int[] Pesos1 = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        private static readonly int[] Pesos2 = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

        /// <summary>Confere os dois dígitos verificadores. Aceita com ou sem máscara.</summary>
        public static bool EhValido(string? valor)
        {
            var digitos = new string((valor ?? "").Where(char.IsDigit).ToArray());
            if (digitos.Length != 14 || digitos.Distinct().Count() == 1)
                return false;

            return digitos[12] - '0' == Digito(digitos, Pesos1) && digitos[13] - '0' == Digito(digitos, Pesos2);
        }

        private static int Digito(string digitos, int[] pesos)
        {
            var soma = pesos.Select((peso, i) => (digitos[i] - '0') * peso).Sum();
            var resto = soma % 11;
            return resto < 2 ? 0 : 11 - resto;
        }
    }
}
