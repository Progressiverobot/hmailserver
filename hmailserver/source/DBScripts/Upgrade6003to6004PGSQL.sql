alter table hm_routes alter column routeauthenticationpassword type varchar(1024);

alter table hm_fetchaccounts alter column fapassword type varchar(1024);

update hm_dbversion set value = 6004;
