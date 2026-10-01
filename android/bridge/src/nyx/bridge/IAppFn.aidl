package nyx.bridge;

// Сервіс у процесі Shizuku (права shell/ADB): виконує вузьку команду
// "cmd app_function ...". id методів задано явно; destroy на спец-транзакції,
// яку Shizuku кличе при зупинці сервіса.
interface IAppFn {
    String run(in String[] args) = 1;
    void destroy() = 16777114;
}
